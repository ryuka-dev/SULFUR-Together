using System;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    // Note: this file uses Plugin.Cfg.LogBossEncounter directly for its presentation logs.
    /// <summary>
    /// Phase 5.4-E: CousinHelper (goblin cousin) is a standalone two-stage boss. Trigger() -> TriggerIntro() ->
    /// Introduction() (camera/teleport/intro) then StartFight() (FightStarted=true). The player-facing entry is
    /// Trigger(); starting from a host event replays Trigger() so the intro + fight start run as one unit.
    /// </summary>
    internal sealed class CousinHelperAdapter : BossAdapterBase
    {
        public override string AdapterName => "CousinHelper";
        protected override string TypeShortName => "CousinHelper";
        protected override string[] TypeFullNames => new[]
        {
            "PerfectRandom.Sulfur.Gameplay.CousinHelper",
            "PerfectRandom.Sulfur.Core.CousinHelper",
        };

        public override bool IsStarted(object component)
            => BossReflect.TryGetBool(component, "FightStarted", out bool v) && v;

        // The cousin's damageable unit is `owner` (the Unit on the CousinHelper GameObject). This is what the boss
        // bar + death track, and what LogOutput25 showed as missing ("客户端没出血条").
        public override object? GetHealthUnit(object component) => BossReflect.GetMember(component, "owner");

        // F3 PROVEN ROOT CAUSE (LogOutput29): on the Client the cousin owner stays `invuln=True`. In vanilla the
        // owner's invulnerability is cleared ONLY by `DoneAppearing()`, an ANIMATION EVENT fired at the end of the
        // intro -> appear animation. Forcing Introduction()/StartFight() sets the "Intro" trigger but the animation
        // chain (and its events: DoneAppearing -> SetInvulnerable(false)+SetUnskippableMovement(false), plus the
        // mechanic events SpawnArmsInLoop/MoveToNewPool) never runs to completion on the Client, so the boss stays
        // invulnerable + inert. The fix is NOT AI activation (proven insufficient) — it is making the Client run the
        // real intro presentation/animation chain (next phase). This method now only DIAGNOSES (activation gated off).
        public override void OnClientPresentationStart(object component)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (!Plugin.Cfg.LogBossEncounter.Value && !Plugin.Cfg.EnableBossClientPresentation.Value) return;
                var owner = BossReflect.GetMember(component, "owner");
                if (owner == null) { Plugin.Log.Info("[BossPresentation] Cousin owner null"); return; }

                var aiAgent = BossReflect.GetMember(owner, "AiAgent") ?? BossReflect.GetMember(owner, "aiAgent");
                string unitState = BossReflect.GetMember(owner, "unitState")?.ToString() ?? "?";
                bool invuln = BossReflect.TryGetBool(owner, "isInvulnerable", out bool iv) && iv;
                Vector3 pos = (owner is Component oc && oc != null) ? oc.transform.position : Vector3.zero;
                BossReflect.TryGetBool(component, "introPlayed", out bool intro);
                BossReflect.TryGetBool(component, "FightStarted", out bool fight);
                BossReflect.TryGetBool(component, "isSubmerged", out bool submerged);
                BossReflect.TryGetBool(component, "waitingToReappear", out bool waiting);
                BossReflect.TryGetBool(component, "HasSpawnedArm", out bool hasArm);
                // animator running? (its events are what would clear invuln / drive the mechanic)
                var animator = BossReflect.GetMember(component, "animator");
                string animState = "?";
                if (animator is Behaviour ab && ab != null) animState = $"enabled={ab.enabled}";

                // F2 shortcut (default OFF now): AI activation — kept only as a toggle, proven insufficient.
                bool activate = false; try { activate = Plugin.Cfg.EnableBossClientPresentation.Value; } catch { }
                string actDetail = "diagnostic-only(activation off)";
                if (activate)
                {
                    BossReflect.TryInvoke(owner, "ActivateBehaviourTree", out string a1);
                    BossReflect.TryInvoke(owner, "MoveToPlayer", out string a2);
                    actDetail = $"activated[{a1}; {a2}]";
                }
                Plugin.Log.Info($"[BossPresentation] Cousin client state: introPlayed={intro} FightStarted={fight} unitState={unitState} OWNER_INVULN={invuln} (cleared only by DoneAppearing anim event) submerged={submerged} waitingReappear={waiting} hasArm={hasArm} animator[{animState}] aiAgent={(aiAgent != null ? "yes" : "no")} pos={pos:F1} -> {actDetail}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossPresentation] Cousin failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---- Phase 5.4-F4 fixed-point pool authority ----
        // Cousin is a fixed-pool event boss: Submerge -> MoveToNewPool(random pool) -> Reappear, all driven by its
        // behaviour tree. The pools are fixed level geometry but `FindObjectsByType(...None)` gives a NON-deterministic
        // array order, so the array INDEX is unstable cross-end — the stable key is the pool's world POSITION. With
        // BossDamageAuthority the client's owner takes no local damage, so its damageUntilSubmerge never drops and it
        // never self-submerges; the Host drives the dig/move and the Client mirrors to the SAME pool position.
        public override string[] DiscreteEventMethods => new[] { "Submerge", "MoveToNewPool", "Reappear" };

        public override bool BuildDiscreteEvent(object component, string eventName, out bool hasPos, out Vector3 pos, out string diag)
        {
            hasPos = false; pos = Vector3.zero;
            var owner = BossReflect.GetMember(component, "owner");
            Vector3 ownerPos = (owner is Component oc && oc != null) ? oc.transform.position : Vector3.zero;
            // The cousin appears at currentPool.cousinPosition; that is the cross-end-stable identity to mirror.
            if (TryGetCurrentPoolPos(component, out Vector3 poolPos)) { hasPos = true; pos = poolPos; }
            int poolCount = (BossReflect.GetMember(component, "pools") is System.Collections.ICollection c) ? c.Count : -1;
            BossReflect.TryGetBool(owner, "isInvulnerable", out bool invuln);
            float dmgLeft = (BossReflect.GetMember(component, "damageUntilSubmerge") is float f) ? f : -1f;
            diag = $"[CousinPool] HOST {eventName} poolPos={(hasPos ? pos.ToString("F1") : "?")} ownerPos={ownerPos:F1} poolCount={poolCount} invuln={invuln} dmgUntilSubmerge={dmgLeft:0}";
            return hasPos;
        }

        public override bool TryApplyDiscreteEvent(object component, string eventName, bool hasPos, Vector3 pos, out string detail)
        {
            switch (eventName)
            {
                case "Submerge":
                    // dig-down presentation (animator ShouldSubmerge + invuln + collider off).
                    BossReflect.TryInvoke(component, "Submerge", out detail);
                    return true;
                case "MoveToNewPool":
                    // DO NOT invoke MoveToNewPool (it would pick a random pool). Mirror the HOST pool: set the local
                    // currentPool to the nearest pool to the host position, and teleport the owner there.
                    detail = MirrorMoveToPool(component, hasPos, pos);
                    return true;
                case "Reappear":
                    BossReflect.TryInvoke(component, "Reappear", out detail);
                    return true;
                case "CousinDeath":
                    detail = ApplyLocalDeath(component);
                    return true;
                default:
                    detail = $"unknown event {eventName}";
                    return false;
            }
        }

        public override bool IsTerminalEvent(string eventName) => eventName == "CousinDeath";

        // LogOutput31: Host kills Cousin normally but the Client only got hp=0 — owner never Died, so the body stays
        // up, killable, bar stuck. The faithful fix is owner.Die() (Unit.Die -> SetUnitState(Dead) + onDeath ->
        // CousinDeath), which runs the REAL encounter-end (bar detach, collider off, elevator down, arm cleanup,
        // music end). We run it under the manager reentry guard so the resulting CousinDeath isn't re-broadcast.
        private string ApplyLocalDeath(object component)
        {
            var owner = BossReflect.GetMember(component, "owner");
            if (owner == null) return "no-owner";
            // Already dead? (IsAlive==false)
            bool alive = !(BossReflect.GetMember(owner, "unitState")?.ToString() == "Dead");
            if (!alive) { BossReflect.TryInvokeBool(owner, "AttachToBossUI", false, out _); return "already-dead (bar detached)"; }

            bool ok = BossReflect.TryInvoke(owner, "Die", out string dieDetail);
            // Defensive: ensure the bar is gone even if CousinDeath path differed on the client.
            BossReflect.TryInvokeBool(owner, "AttachToBossUI", false, out _);
            return $"owner.Die()={ok} ({dieDetail}); bossUI detached";
        }

        private string MirrorMoveToPool(object component, bool hasPos, Vector3 pos)
        {
            if (!hasPos) return "no-pos";
            var owner = BossReflect.GetMember(component, "owner");
            // Find the local pool whose cousinPosition is nearest the host's appear position (pools are identical
            // geometry on both ends; nearest-by-position is the stable cross-end match).
            object? bestPool = null; float bestDist = float.MaxValue;
            if (BossReflect.GetMember(component, "pools") is System.Collections.IEnumerable pools)
            {
                foreach (var p in pools)
                {
                    if (!TryGetPoolPos(p, out Vector3 pp)) continue;
                    float d = Vector3.Distance(pp, pos);
                    if (d < bestDist) { bestDist = d; bestPool = p; }
                }
            }
            string setDetail = "no-pool";
            if (bestPool != null) BossReflect.TryInvokeArg(component, "SetNewPool", bestPool, out setDetail);
            string tpDetail = "no-owner";
            if (owner != null) BossReflect.TryInvokeArg(owner, "TeleportTo", pos, out tpDetail);
            return $"mirror pool(dist={bestDist:0.0}) set[{setDetail}] tp[{tpDetail}]";
        }

        private static bool TryGetCurrentPoolPos(object component, out Vector3 pos)
        {
            pos = Vector3.zero;
            var cur = BossReflect.GetMember(component, "currentPool");
            return cur != null && TryGetPoolPos(cur, out pos);
        }

        private static bool TryGetPoolPos(object pool, out Vector3 pos)
        {
            pos = Vector3.zero;
            var cp = BossReflect.GetMember(pool, "cousinPosition"); // Transform
            if (cp is Transform t && t != null) { pos = t.position; return true; }
            if (pool is Component pc && pc != null) { pos = pc.transform.position; return true; }
            return false;
        }

        // Trigger() -> TriggerIntro() (sets triggeredByPlayer); then dialogue/animation events drive Introduction()
        // (intro/teleport, introPlayed=true) and finally StartFight() (FightStarted=true). Introduction/StartFight
        // are PLAYER-PACED (dialogue), so they can fire seconds later — the continuation window (closed by start
        // observed, not a fixed timeout) must keep them allowed the whole time.
        public override string[] StartChainMethods => new[] { "Trigger", "TriggerIntro", "Introduction", "StartFight" };

        // Cousin is dialog-gated: the player ends a NodeCanvas dialogue ("attack" choice) which drives the start
        // chain. Without commit sync the client's local dialogue re-opens after FightStarted (LogOutput21).
        public override bool IsDialogBoss(object component) => true;

        // Phase PF (Plan B): the Cousin behavior-tree sequence is `triggeredByPlayer -> Introduction -> 2s ->
        // Npc.Interact (dialog) -> 1s -> SpawnArm -> 0.1s -> StartFight`. The class itself never gates on the dialog —
        // in single-player the dialog PAUSES the game (timeScale=0), freezing the WaitForSeconds so StartFight only
        // runs once the player dismisses the dialog. Co-op disables that pause (Phase 5.7-NP), so StartFight fires
        // ~1.1s after the dialog opens and overlaps it. We block StartFight until the dialog is dismissed (commit).
        public override bool GatesFightOnDialogClose(object component) => true;

        // Introduction is the earliest unambiguous "fight chosen" step; StartFight is the definitive one.
        public override bool IsDialogCommitSource(string source)
        {
            var m = ParseSourceMethod(source);
            return m == "Introduction" || m == "StartFight";
        }

        // Suppress ONLY once the intro has actually played (introPlayed). The earlier FightStarted check was the
        // LogOutput24 bug: the commit set FightStarted before introPlayed, so every Introduction was suppressed,
        // introPlayed never got set, and the client's NodeCanvas dialog graph looped reopening "大表哥" forever.
        public override bool ShouldSuppressDuplicateDialogEntry(object component, string source)
        {
            if (!(BossReflect.TryGetBool(component, "introPlayed", out bool intro) && intro)) return false;
            var m = ParseSourceMethod(source);
            return m == "Trigger" || m == "TriggerIntro" || m == "Introduction";
        }

        public override bool TryApplyDialogCommit(object component, NetBossDialogCommit commit, out string detail)
        {
            // Phase PF FAITHFUL INTRO: instead of fake-starting via direct Introduction()/StartFight() (which skips the
            // real intro dialog — the client only ever saw the teleport, never the "Better get this over with" dialog),
            // set the boss's own trigger flag via TriggerIntro() so its NATIVE behavior-tree sequence runs locally:
            //   triggeredByPlayer -> Introduction() -> 2s -> Npc.Interact() [REAL dialog] -> SpawnArm -> AttachToBossUI + StartFight().
            // The manager has opened the continuation window, so the behavior tree's own Introduction/StartFight calls
            // are allowed through (not blocked). Reproduces the real intro+dialog+camera+bar ~99% faithfully; the fight
            // mechanic stays host-authoritative (BossState / CousinPool / BossDamageAuthority). We do NOT finalize the
            // dialog here — we WANT it to play.
            bool faithful = false; try { faithful = Plugin.Cfg.EnableFaithfulBossIntro.Value; } catch { }
            if (faithful)
            {
                string trigDetail = "intro-already-played";
                if (!(BossReflect.TryGetBool(component, "introPlayed", out bool already) && already))
                    BossReflect.TryInvoke(component, "TriggerIntro", out trigDetail); // sets triggeredByPlayer; behavior tree runs the rest
                detail = $"faithful: TriggerIntro={trigDetail} (native behavior-tree intro+dialog will play; mechanic host-authoritative)";
                return true;
            }

            // Legacy (fast-start) path: drive the chain directly + finalize the dialog (no real intro dialog shown).
            string introDetail = "intro-already-played";
            if (!(BossReflect.TryGetBool(component, "introPlayed", out bool intro) && intro))
                BossReflect.TryInvoke(component, "Introduction", out introDetail);
            string startDetail = "already-started";
            if (!IsStarted(component))
                BossReflect.TryInvoke(component, "StartFight", out startDetail);
            bool finalized = BossDialogReflect.TryFinalizeCurrentDialog(out string dlgDetail);
            detail = $"intro={introDetail}; start={startDetail}; dialogFinalized={finalized} ({dlgDetail})";
            return true;
        }

        public override string DescribeForLog(object component)
        {
            bool started = IsStarted(component);
            BossReflect.TryGetBool(component, "introPlayed", out bool introPlayed);
            BossReflect.TryGetBool(component, "isSubmerged", out bool submerged);
            BossReflect.TryGetInt(component, "stagesDone", out int stages);
            var owner = BossReflect.GetMember(component, "owner");
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} FightStarted={started} introPlayed={introPlayed} submerged={submerged} stagesDone={stages} owner={(owner != null ? "yes" : "no")}";
        }
    }
}
