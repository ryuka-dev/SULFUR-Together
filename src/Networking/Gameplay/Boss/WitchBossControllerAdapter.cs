using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: WitchBossController is a standalone boss system (NOT a BossFightHelper subclass).
    /// Start entrypoints: EventStarted() (starts FightStartRoutine intro) and StartFight() (sets fightStarted,
    /// attaches the boss UI). Health lives on witchMainUnit. Phase is currentPhase (a WitchPhase enum).
    /// </summary>
    internal sealed class WitchBossControllerAdapter : BossAdapterBase
    {
        public override string AdapterName => "WitchBossController";
        protected override string TypeShortName => "WitchBossController";
        protected override string[] TypeFullNames => new[]
        {
            "PerfectRandom.Sulfur.Gameplay.WitchBossController",
            "PerfectRandom.Sulfur.Core.WitchBossController",
        };

        public override bool IsStarted(object component)
            => BossReflect.TryGetBool(component, "fightStarted", out bool v) && v;

        // EventStarted() (public, player entry) -> FightStartRoutine coroutine (spans frames) -> StartFight()
        // (PRIVATE; sets fightStarted + AttachToBossUI). StartFight fires across frames AFTER the synchronous apply
        // returns, so it must be allowed by the continuation window rather than blocked as a fresh local start.
        public override string[] StartChainMethods => new[] { "EventStarted", "StartFight" };

        public override void TryReadState(object component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos)
        {
            hasPhase = BossReflect.TryGetInt(component, "currentPhase", out phaseIndex); // WitchPhase enum -> int
            hasPos = BossReflect.TryGetPosition(component, out pos);
        }

        // ---- Phase 5.4-E3 phase/state broadcast (minimal skeleton) ----
        // WitchPhase enum: Waiting=0, Intro=1, Phase1_EightFlying=2, ... Phase6_EggLaying=7, Dead=8.
        // currentPhase is the phase core; health lives on witchMainUnit; aliveAdds are the spawned illusions/adds.
        public override bool ProvidesPhaseState => true;

        // The witch's real damageable unit (drives the boss bar + death). currentPhase advances on the client by its
        // own mechanic completion (LogOutput25), so health is the actual divergent state to sync.
        public override object? GetHealthUnit(object component) => BossReflect.GetMember(component, "witchMainUnit");

        public override void FillBossState(object component, NetBossState state)
        {
            base.FillBossState(component, state); // health from GetHealthUnit
            BossReflect.TryGetInt(component, "currentPhase", out int phase);
            state.PhaseIndex = phase;
            state.FightStarted = BossReflect.TryGetBool(component, "fightStarted", out bool fs) && fs;
            state.IntroFinished = BossReflect.TryGetBool(component, "isIntroFinished", out bool intro) && intro;
            var adds = BossReflect.GetMember(component, "aliveAdds");
            if (adds is ICollection col) state.AliveAdds = col.Count;
        }

        // Phase 5.4-G2: BossState now syncs ONLY health. Phase is owned by the dedicated WitchPhase revision authority
        // (NetWitchPhase) — the old forward-only `state.PhaseIndex <= localPhase` comparison was WRONG because Witch
        // phases CYCLE (Phase6→Phase1), so a smaller enum is not an older state. Applying phase here too would fight
        // the revision authority and block the cycle. Health stays on the periodic BossState stream.
        public override bool TryApplyBossState(object component, NetBossState state, out string detail)
        {
            detail = ApplyHealth(component, state) + "; phase via WitchPhase-revision";
            return true;
        }

        // ---- Phase 5.4-G2: Witch phase revision authority helpers ----
        // currentPhase is a WitchPhase enum (Waiting=0,Intro=1,Phase1=2,...,Phase6=7,Dead=8). Each combat phase maps to
        // a WitchPhaseController field (phase1..phase6). The phase transition chokepoint is WitchBossController.ChangePhase
        // (every WitchPhaseController.EndPhase calls bossController.ChangePhase(nextPhaseAfterThis)).
        public int GetCurrentPhase(object component)
            => BossReflect.TryGetInt(component, "currentPhase", out int p) ? p : -1;

        private static string? PhaseEnumToControllerField(int phase) => phase switch
        {
            2 => "phase1", 3 => "phase2", 4 => "phase3", 5 => "phase4", 6 => "phase5", 7 => "phase6", _ => null,
        };

        /// <summary>Tear down the currently-active local phase to match the Host before entering the new one. Calls the
        /// phase controller's real EndPhase (deactivates + disables its witch); EndPhase's inner ChangePhase(next) is
        /// blocked because this runs OUTSIDE the reentry guard. No-op for Intro/Waiting/Dead.</summary>
        public bool EndCurrentWitchPhase(object component, out string detail)
        {
            int p = GetCurrentPhase(component);
            var f = PhaseEnumToControllerField(p);
            if (f == null) { detail = $"no controller for phase {p} (skip teardown)"; return false; }
            var ctrl = BossReflect.GetMember(component, f);
            if (ctrl == null) { detail = $"{f} controller null"; return false; }
            string p2 = "";
            // Phase 5.4-G3: WitchPhase2.EndPhase only does base.EndPhase (phaseActive=false) — it does NOT hide the dome
            // witches (the Host hides them via RealWitchTakeDamage→IllusionsDisappearAll + EightFlying). On the Client
            // that real-hit chain never ran, so the dome real/illusions would linger across phases (LogOutput35: clones
            // persist and pollute later phases). Hide them all here when the Host transitions out of Phase 2.
            if (p == 3) { int hidden = HidePhase2Witches(component); ClearPhase2State(); p2 = $"; hidPhase2Witches={hidden} p2StateCleared"; }
            if (!(BossReflect.TryGetBool(ctrl, "phaseActive", out bool active) && active)) { detail = $"{f} already inactive{p2}"; return p2.Length > 0; }
            bool ok = BossReflect.TryInvoke(ctrl, "EndPhase", out string d);
            detail = $"EndPhase({f})={ok} {d}{p2}";
            return ok;
        }

        /// <summary>Hide (NOT destroy) every Phase 2 dome witch — real and illusions — so none linger past Phase 2:
        /// disable the hitmesh collider (so the ordinary ClientHit pipeline can't claim it later) and play the Disappear
        /// animation, and drop it from GameManager.aliveNpcs (targeting). Non-destructive so the next Phase 2 cycle's
        /// ShowWitches can re-show the same objects. Returns the number hidden.</summary>
        public int HidePhase2Witches(object component)
        {
            int hidden = 0;
            try
            {
                var phase2 = BossReflect.GetMember(component, "phase2");
                if (phase2 == null) return 0;
                if (!(BossReflect.GetMember(phase2, "spawnedWitches") is IList list)) return 0;
                object? aliveNpcs = TryGetAliveNpcs();
                foreach (var w in list)
                {
                    if (!(w is UnityEngine.Object uo) || uo == null) continue;
                    var col = BossReflect.GetMember(w, "hitmeshCollider");
                    if (col is Collider c && c != null) { try { c.enabled = false; } catch { } }
                    var anim = BossReflect.GetMember(w, "animator");
                    if (anim is Animator a && a != null) { try { a.SetBool("IsDisappeared", true); a.SetTrigger("Disappear"); a.ResetTrigger("Appear"); } catch { } }
                    if (aliveNpcs is IList al) { try { al.Remove(w); } catch { } }
                    hidden++;
                }
            }
            catch { }
            return hidden;
        }

        private static object? TryGetAliveNpcs()
        {
            try
            {
                var gmType = BossReflect.FindType("GameManager", "PerfectRandom.Sulfur.Core.GameManager", "PerfectRandom.Sulfur.Gameplay.GameManager");
                var instProp = gmType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                var gm = instProp?.GetValue(null);
                return gm == null ? null : BossReflect.GetMember(gm, "aliveNpcs");
            }
            catch { return null; }
        }

        /// <summary>Enter the Host's phase via the real ChangePhase. Must be called UNDER the manager's reentry guard so
        /// the prefix lets it through (the Client otherwise blocks all local ChangePhase).</summary>
        public bool ApplyHostPhase(object component, int phaseInt, out string detail)
            => BossReflect.TryInvokeWithEnumInt(component, "ChangePhase", phaseInt, out detail);

        /// <summary>Diagnostic: does the active phase controller currently have a spawned witchUnit (the damage target)?</summary>
        public bool ActivePhaseWitchExists(object component)
        {
            var f = PhaseEnumToControllerField(GetCurrentPhase(component));
            if (f == null) return false;
            return GetPhaseWitchUnit(component, f) != null;
        }

        // ---- Phase 5.4-G: visible phase-witch damage routing ----
        // Confirmed by decompilation of WitchBossController + WitchPhaseN: the player does NOT shoot witchMainUnit.
        // Each phase has a controller field (phase1/phase3/phase4/phase5/phase6 — phase2 is the dome, reserved) whose
        // private `witchUnit` (Npc) is the visible target. That unit's onDamageRecieved is wired to
        // bossController.OnDamageMainWitch (→ witchMainUnit.ReceiveDamage) AND, for Phase 4, to RegisterInstance
        // (the hit-count mechanic that drives GoDown). So the Host MUST call ReceiveDamage on the phase witchUnit —
        // NOT witchMainUnit — or the phase mechanic (e.g. Phase 4 count) is skipped. The role string is the controller
        // field name ("phase4"); both ends resolve the same field → its current witchUnit (instances differ per end).
        // Phase 2 (real/illusion dome) is deliberately excluded here and reserved for a dedicated manifest.
        private static readonly string[] PhaseWitchControllerFields = { "phase1", "phase3", "phase4", "phase5", "phase6" };

        private static bool WitchPhaseDamageEnabled
        {
            get { try { return Plugin.Cfg.EnableWitchPhaseDamageAuthority.Value; } catch { return false; } }
        }

        /// <summary>The visible witch Unit of a phase controller field (phase1..phase6), or null if not spawned/active.</summary>
        private static object? GetPhaseWitchUnit(object component, string controllerField)
        {
            var ctrl = BossReflect.GetMember(component, controllerField);
            return ctrl == null ? null : BossReflect.GetMember(ctrl, "witchUnit");
        }

        /// <summary>CLIENT: the player hit a local Unit. If it is one of the phase controllers' visible witchUnit, return
        /// that phase's role (the controller field name) so the Host damages its OWN matching phase witch. witchMainUnit
        /// itself maps to "main". Phase 2 dome witches are not matched here (reserved).</summary>
        public override string? ResolveHitTargetRole(object component, object hitUnit)
        {
            if (hitUnit == null || !WitchPhaseDamageEnabled) return null;
            // Phase 5.4-G5: a Phase 2 dome witch (real OR illusion) → "p2dome:<domeIndex>". The Host damages its OWN
            // spawnedWitches[domeIndex], which is real iff domeIndex==realDomeIndex on both ends (manifest-synced).
            if (_p2DomeByInstance.TryGetValue(BossReflect.InstanceId(hitUnit), out int dome)) return P2DomeRole + dome;
            var main = GetHealthUnit(component);
            if (main != null && ReferenceEquals(main, hitUnit)) return "main";
            foreach (var f in PhaseWitchControllerFields)
            {
                var wu = GetPhaseWitchUnit(component, f);
                if (wu != null && ReferenceEquals(wu, hitUnit)) return f;
            }
            return null;
        }

        /// <summary>HOST: resolve a role to the real Unit to damage. "main" → witchMainUnit (base). "phaseN" → that
        /// controller's current witchUnit, so its onDamageRecieved (OnDamageMainWitch + phase mechanic) runs natively.</summary>
        public override object? ResolveHostTargetForRole(object component, string role)
        {
            if (string.IsNullOrEmpty(role)) return null;
            if (role == "main") return base.ResolveHostTargetForRole(component, role);
            if (!WitchPhaseDamageEnabled) return null;
            // Phase 5.4-G5: "p2dome:N" → the Host's dome-N witch = spawnedWitches[N] (ShowWitches placed it at domePositions[N]).
            if (role.StartsWith(P2DomeRole, StringComparison.Ordinal) && int.TryParse(role.Substring(P2DomeRole.Length), out int dome))
                return GetHostDomeWitch(component, dome);
            foreach (var f in PhaseWitchControllerFields)
                if (role == f) return GetPhaseWitchUnit(component, f);
            return null;
        }

        // ================================================================== Phase 5.4-G5: Witch Phase 2 dome manifest
        // Audit-confirmed: ShowWitches does `spawnedWitches[i].position = domePositions[i]` after a second shuffle, so the
        // HOST's dome N == spawnedWitches[N] and the final real dome = spawnedWitches.IndexOf(realWitchUnit). The two
        // host-local randoms (SpawnWitches realIndex + ShowWitches shuffle) make the real witch land on a DIFFERENT dome
        // per end, so the Client must mirror the Host layout: put ITS realWitchUnit at the host real dome, illusions on
        // the rest. Client Phase 2 witches are then isolated from the ordinary puppet transform (see manager) so the
        // manifest placement holds. State is static (one Witch encounter at a time).
        private const string P2DomeRole = "p2dome:";
        private static readonly System.Collections.Generic.Dictionary<int, object> _p2DomeUnit = new System.Collections.Generic.Dictionary<int, object>(); // domeIndex -> local Npc
        private static readonly System.Collections.Generic.Dictionary<int, int> _p2DomeByInstance = new System.Collections.Generic.Dictionary<int, int>(); // local Npc instanceId -> domeIndex
        private static readonly HashSet<int> _p2Suppressed = new HashSet<int>(); // local Phase 2 witch instanceIds to skip in the ordinary puppet transform
        private static int _p2RealDome = -1;

        private static bool WitchP2ManifestEnabled
        {
            get { try { return Plugin.Cfg.EnableWitchPhase2Manifest.Value; } catch { return false; } }
        }

        /// <summary>Static membership check used by the ordinary puppet transform loop to SKIP Phase 2 witches while the
        /// manifest controls them (otherwise the snapshot pulls them off their dome positions).</summary>
        public static bool IsInstancePhase2Suppressed(int instanceId) => _p2Suppressed.Contains(instanceId);

        public static void ClearPhase2State()
        {
            _p2DomeUnit.Clear(); _p2DomeByInstance.Clear(); _p2Suppressed.Clear(); _p2RealDome = -1;
        }

        /// <summary>HOST: the final real dome (after the ShowWitches shuffle).</summary>
        public int GetHostRealDomeIndex(object component)
        {
            var p2 = BossReflect.GetMember(component, "phase2");
            var real = BossReflect.GetMember(p2, "realWitchUnit");
            if (real != null && BossReflect.GetMember(p2, "spawnedWitches") is IList list)
                for (int i = 0; i < list.Count; i++)
                    if (ReferenceEquals(list[i], real)) return i;
            return -1;
        }

        public int GetHostDomeCount(object component)
        {
            var p2 = BossReflect.GetMember(component, "phase2");
            return BossReflect.GetMember(p2, "spawnedWitches") is ICollection c ? c.Count : -1;
        }

        private object? GetHostDomeWitch(object component, int dome)
        {
            var p2 = BossReflect.GetMember(component, "phase2");
            if (BossReflect.GetMember(p2, "spawnedWitches") is IList list && dome >= 0 && dome < list.Count) return list[dome];
            return null;
        }

        /// <summary>True once the local Phase 2 witches are spawned and match the dome count (ready to mirror the manifest).</summary>
        public bool IsPhase2Ready(object component, int domeCount)
        {
            var p2 = BossReflect.GetMember(component, "phase2");
            if (p2 == null) return false;
            if (!(BossReflect.TryGetBool(p2, "witchesCreated", out bool wc) && wc)) return false;
            int sw = BossReflect.GetMember(p2, "spawnedWitches") is ICollection c ? c.Count : -1;
            int dome = BossReflect.GetMember(p2, "domePositions") is ICollection d ? d.Count : -1;
            return sw == domeCount && dome == domeCount && BossReflect.GetMember(p2, "realWitchUnit") != null;
        }

        /// <summary>CLIENT: mirror the Host dome layout — put the local realWitchUnit at <paramref name="realDomeIndex"/>,
        /// illusions on the other domes, show them, and record the dome↔unit mapping + suppression set. Idempotent.</summary>
        public bool ApplyP2Manifest(object component, int realDomeIndex, int domeCount, out int placed, out string detail)
        {
            placed = 0; detail = "";
            var p2 = BossReflect.GetMember(component, "phase2");
            if (p2 == null) { detail = "no phase2 controller"; return false; }
            var real = BossReflect.GetMember(p2, "realWitchUnit");
            if (!(BossReflect.GetMember(p2, "spawnedIllusions") is IList illusions)
                || !(BossReflect.GetMember(p2, "domePositions") is IList domes) || real == null)
            { detail = "local witches not ready"; return false; }
            if (domes.Count < domeCount) { detail = $"domePositions {domes.Count} < {domeCount}"; return false; }

            ClearPhase2State();
            _p2RealDome = realDomeIndex;
            object? aliveNpcs = TryGetAliveNpcs();

            // Real witch at the host real dome.
            if (realDomeIndex >= 0 && realDomeIndex < domes.Count)
            { if (PlaceAndShowDome(p2, real, domes[realDomeIndex], aliveNpcs)) placed++; RecordDome(realDomeIndex, real); }

            // Illusions fill the remaining domes in order.
            int illIdx = 0;
            for (int d = 0; d < domeCount; d++)
            {
                if (d == realDomeIndex) continue;
                if (illIdx >= illusions.Count) break;
                var ill = illusions[illIdx++];
                if (ill == null) continue;
                if (PlaceAndShowDome(p2, ill, domes[d], aliveNpcs)) placed++;
                RecordDome(d, ill);
            }
            detail = $"placed={placed} realDome={realDomeIndex} domes={domeCount} suppressed={_p2Suppressed.Count}";
            return true;
        }

        private void RecordDome(int dome, object unit)
        {
            _p2DomeUnit[dome] = unit;
            int id = BossReflect.InstanceId(unit);
            _p2DomeByInstance[id] = dome;
            _p2Suppressed.Add(id);
        }

        private bool PlaceAndShowDome(object p2, object npc, object domeTransform, object? aliveNpcs)
        {
            try
            {
                if (npc is Component nc && nc != null && domeTransform is Transform dt && dt != null)
                    nc.transform.position = dt.position;
                InvokeEnableWitchUnit(p2, npc, true);
                if (aliveNpcs is IList al && !al.Contains(npc)) { try { al.Add(npc); } catch { } }
                // Hard-guarantee hittable + visible (in case the EnableWitchUnit coroutine lags).
                var col = BossReflect.GetMember(npc, "hitmeshCollider");
                if (col is Collider c && c != null) { try { c.enabled = true; } catch { } }
                var anim = BossReflect.GetMember(npc, "animator");
                if (anim is Animator a && a != null) { try { a.SetBool("IsDisappeared", false); } catch { } }
                return true;
            }
            catch { return false; }
        }

        /// <summary>CLIENT: apply a Host Phase 2 hit result. IllusionDefeated → hide that dome; RealHit → hide all illusions
        /// (the real witch stays until the phase ends).</summary>
        public bool ApplyP2Result(object component, int domeIndex, byte kind, out string detail)
        {
            var p2 = BossReflect.GetMember(component, "phase2");
            object? aliveNpcs = TryGetAliveNpcs();
            if (kind == NetWitchP2Result.KindRealHit)
            {
                int hidden = 0;
                foreach (var kv in _p2DomeUnit)
                {
                    if (kv.Key == _p2RealDome) continue; // keep the real witch
                    if (HideDomeUnit(kv.Value, aliveNpcs)) hidden++;
                }
                detail = $"realHit hideAllIllusions hidden={hidden} (realDome={_p2RealDome} kept)";
                return true;
            }
            // single illusion defeated
            if (_p2DomeUnit.TryGetValue(domeIndex, out var unit))
            { bool ok = HideDomeUnit(unit, aliveNpcs); detail = $"illusionDefeated dome={domeIndex} hidden={ok}"; return ok; }
            detail = $"illusionDefeated dome={domeIndex} not in assignment";
            return false;
        }

        private bool HideDomeUnit(object npc, object? aliveNpcs)
        {
            try
            {
                if (!(npc is UnityEngine.Object uo) || uo == null) return false;
                var col = BossReflect.GetMember(npc, "hitmeshCollider");
                if (col is Collider c && c != null) { try { c.enabled = false; } catch { } }
                var anim = BossReflect.GetMember(npc, "animator");
                if (anim is Animator a && a != null) { try { a.SetBool("IsDisappeared", true); a.SetTrigger("Disappear"); a.ResetTrigger("Appear"); } catch { } }
                if (aliveNpcs is IList al) { try { al.Remove(npc); } catch { } }
                return true;
            }
            catch { return false; }
        }

        // ================================================================== Phase 5.4-G7b: Witch death replica (client)
        // The client runs WitchDeath via the enemy death mirror, but WitchDeath reads EquipmentManager.AmuletHoldable
        // → GetHoldableInSlot(Amulet) → equippedItems[Amulet], which the client never equipped → KeyNotFoundException
        // ("Amulet"), aborting the rest of WitchDeath. Returning null from the getter just moves the crash to
        // null.GetComponent. So on the client we BLOCK the original WitchDeath and replay the safe subset here, skipping
        // ONLY the amulet block + PlayerProgress (host-only). ChangePhase(Dead) is left to the G2 host phase broadcast.
        public bool TryApplyWitchDeath(object component, out string detail)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("teleportBack=").Append(TryStartPrivateCoroutine(component, "TeleportDeathDelay"));
            var witchMain = BossReflect.GetMember(component, "witchMainUnit");
            sb.Append(" barOff=").Append(BossReflect.TryInvokeBool(witchMain, "AttachToBossUI", false, out _));
            sb.Append(" churchFire=").Append(BossReflect.TryInvoke(BossReflect.GetMember(component, "fireSequence"), "StartChurchFire", out _));
            TrySetAnimatorTrigger(BossReflect.GetMember(component, "outsideFireAnimator"), "StartEnd");
            // MusicTrigger.StopMusic(float fadeDuration=2f) has a parameter, so the parameterless TryInvoke missed it
            // (LogOutput41 musicStop=False). Invoke it with a single float so the client's boss music actually stops.
            sb.Append(" musicStop=").Append(TryInvokeSingleFloat(BossReflect.GetMember(component, "musicTrigger"), "StopMusic", 2f));
            TrySetBoolField(component, "fightStarted", false);
            TrySetActiveGO(BossReflect.GetMember(component, "blockadesToActivateAfterFight"), true);
            TrySetActiveGO(BossReflect.GetMember(component, "fireEffectRoot"), true);
            BossReflect.TryInvokeBool(BossReflect.GetMember(component, "followRain"), "ShouldEnableRain", true, out _);
            BossReflect.TryInvoke(BossReflect.GetMember(component, "lightningSpawner"), "StartLightning", out _);
            BossReflect.TryInvoke(BossReflect.GetMember(component, "witchAnimationControl"), "EnableChurchBlockade", out _);
            sb.Append(" adds=").Append(BossReflect.TryInvoke(component, "DeactivateAllAdds", out _));
            // (fire-particle emission restore is skipped — cosmetic only, avoids a ParticleSystemModule reference)
            detail = sb.ToString();
            return true;
        }

        private static bool TryStartPrivateCoroutine(object component, string method)
        {
            try
            {
                if (!(component is MonoBehaviour mb) || mb == null) return false;
                for (Type? t = component.GetType(); t != null; t = t.BaseType)
                {
                    var mi = t.GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (mi != null && typeof(IEnumerator).IsAssignableFrom(mi.ReturnType))
                    {
                        if (mi.Invoke(component, null) is IEnumerator ie) { mb.StartCoroutine(ie); return true; }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void TrySetAnimatorTrigger(object? animator, string trigger)
        { try { if (animator is Animator a && a != null) a.SetTrigger(trigger); } catch { } }

        /// <summary>Invoke an instance method that takes a single float (e.g. MusicTrigger.StopMusic(float)).</summary>
        private static bool TryInvokeSingleFloat(object? obj, string method, float arg)
        {
            if (obj == null) return false;
            try
            {
                for (Type? t = obj.GetType(); t != null; t = t.BaseType)
                {
                    var mi = t.GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(float) }, null);
                    if (mi != null) { mi.Invoke(obj, new object[] { arg }); return true; }
                }
            }
            catch { }
            return false;
        }

        private static void TrySetActiveGO(object? go, bool active)
        {
            try
            {
                if (go is GameObject g && g != null) g.SetActive(active);
                else if (go is Component c && c != null) c.gameObject.SetActive(active);
            }
            catch { }
        }

        private static void TrySetBoolField(object obj, string name, bool value)
        {
            try
            {
                for (Type? t = obj.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null && f.FieldType == typeof(bool)) { f.SetValue(obj, value); return; }
                }
            }
            catch { }
        }

        /// <summary>Invoke the protected WitchPhaseController.EnableWitchUnit(Npc, bool enable, bool playSound).</summary>
        private static void InvokeEnableWitchUnit(object controller, object npc, bool enable)
        {
            try
            {
                for (Type? t = controller.GetType(); t != null; t = t.BaseType)
                {
                    foreach (var mi in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (mi.Name != "EnableWitchUnit") continue;
                        var ps = mi.GetParameters();
                        if (ps.Length == 3 && ps[0].ParameterType.IsInstanceOfType(npc) && ps[1].ParameterType == typeof(bool) && ps[2].ParameterType == typeof(bool))
                        { mi.Invoke(controller, new object[] { npc, enable, false }); return; }
                    }
                }
            }
            catch { }
        }

        /// <summary>Phase 5.4-E3 diagnostic: which Witch phase objects are active and which is the real damageable unit.</summary>
        public string DescribeAddManifest(object component)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var f in new[] { "phase1", "phase2", "phase3", "phase4", "phase5", "phase6" })
            {
                var pc = BossReflect.GetMember(component, f);
                if (pc is Component c && c != null) parts.Add($"{f}={(c.gameObject.activeInHierarchy ? "active" : "inactive")}");
            }
            var witch = BossReflect.GetMember(component, "witchMainUnit");
            string witchName = (witch is Component wc && wc != null) ? wc.gameObject.name : "?";
            var adds = BossReflect.GetMember(component, "aliveAdds");
            int addCount = adds is ICollection col ? col.Count : -1;
            return $"witchMainUnit(real-damageable)={witchName} aliveAdds={addCount} {string.Join(" ", parts)}";
        }

        public override string DescribeForLog(object component)
        {
            bool started = IsStarted(component);
            bool hasPhase = BossReflect.TryGetInt(component, "currentPhase", out int phase);
            BossReflect.TryGetBool(component, "isIntroFinished", out bool introDone);
            var witchUnit = BossReflect.GetMember(component, "witchMainUnit");
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} fightStarted={started} currentPhase={(hasPhase ? phase.ToString() : "?")} introFinished={introDone} witchMainUnit={(witchUnit != null ? "yes" : "no")}";
        }
    }
}
