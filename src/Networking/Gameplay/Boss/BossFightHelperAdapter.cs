using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: handles every BossFightHelper-derived boss (DesertClauseBossFightHelper,
    /// TerrorbaumBossFightHelper, LuciaBossFightHelper, ...). Start = BossFightHelper.TriggerFight(), which the
    /// reverse-engineering confirms sets fightStarted, attaches the boss UI, starts boss phases and loot/music.
    /// </summary>
    internal sealed class BossFightHelperAdapter : BossAdapterBase
    {
        public override string AdapterName => "BossFightHelper";
        protected override string TypeShortName => "BossFightHelper";
        protected override string[] TypeFullNames => new[]
        {
            "PerfectRandom.Sulfur.Core.BossFightHelper",
            "PerfectRandom.Sulfur.Gameplay.BossFightHelper",
        };

        public override bool IsStarted(object component)
            => BossReflect.TryGetBool(component, "fightStarted", out bool v) && v;

        // BossFightHelper / DesertClause / Lucia all damage `bossUnit` (the boss bar + death unit).
        public override object? GetHealthUnit(object component) => BossReflect.GetMember(component, "bossUnit");

        // LD-Sandstorm: DesertClause fights inside a gate-less sandstorm ring — a moving SphereCollider "danger zone".
        // Decompiled DesertClausePerimeter: outside iff Distance(unit, sphereCollider.transform.position) > SphereRadius,
        // where SphereRadius = sphereCollider.radius * |lossyScale.x| (a live public property). We read the sphere's
        // world position + that property so the in/out test tracks the sphere as it moves / resizes. Only DesertClause
        // has OnStartInteractWithBoss among BossFightHelper types (Terrorbaum/Lucia use TriggerFight).
        public override bool TryGetSandstormArenaSphere(object component, out Vector3 center, out float radius)
        {
            center = Vector3.zero; radius = 0f;
            if (!BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return false; // DesertClause only
            var perimeter = BossReflect.GetMember(component, "desertClausePerimeter");
            if (perimeter == null) return false;
            // Live radius from the perimeter's SphereRadius property (radius * lossyScale.x).
            BossReflect.TryGetFloat(perimeter, "SphereRadius", out radius);
            // Centre = the sphereCollider's world position (the moving danger-zone sphere).
            var sphere = BossReflect.GetMember(perimeter, "sphereCollider");
            if (sphere is Component sc && sc != null)
            {
                center = sc.transform.position;
                if (radius <= 0f) radius = sc.transform.lossyScale.x * 0.5f; // fallback: unit-sphere radius 0.5 * scale
                return radius > 0f;
            }
            // Fallback: the perimeter root / boss body sits at the arena centre.
            if (perimeter is Component pc && pc != null) center = pc.transform.position;
            else if (GetHealthUnit(component) is Component bc && bc != null) center = bc.transform.position;
            else return false;
            return radius > 0f;
        }

        // ---- LD-Sandstorm / F4-MISSILE D2: homing-missile visual multiplayer ----
        private static Type? _rocketType;
        private static MethodInfo? _rocketInit, _autoPoolGetInstance, _autoPoolReleaseGO, _psPlay;
        private static Type? _autoPoolType, _psType;
        private static UnityEngine.Object? _autoPool;
        private static GameObject? _ghostDummyMarker; // inert fallback if the real marker can't be fetched (Initialize NREs on null)
        private static bool _d2Resolved;
        private static float _ghostRocketLogNext;

        /// <summary>F4-MISSILE D2: the boss just fired a real homing rocket at the LOCAL player (native SpawnRealRocket
        /// hard-codes GameManager.PlayerUnit). Spawn a ghost VISUAL rocket homing on each remote player's visual proxy
        /// (both ends have proxies — the host shows client capsules too), so every end sees every player hunted. The
        /// ghost's damage pass is skipped via the manager's ghost registry (a ghost explosion must not double-damage the
        /// player already hit by their own local real rocket — Log333 confirmed each end takes its own); the rocket still
        /// falls/explodes natively and pools itself back (DestroyMissile → TryPool). Pitfalls hit before (Log335):
        /// AutoPool.GetInstance returns an AutoPooledObject COMPONENT (not a GameObject — the `as GameObject` cast nulled
        /// out silently), and GameManager.Players has no entry for remote players on the client (proxies are the correct
        /// enumeration). Fail-open: any resolution miss just skips the visuals.</summary>
        public void SpawnGhostVisualRockets(object missileBase)
        {
            try
            {
                if (!(missileBase is Component)) return;
                var missilePrefab = BossReflect.GetMember(missileBase, "missilePrefab") as GameObject;
                var markerPrefab = BossReflect.GetMember(missileBase, "missileMarkerPrefab") as GameObject;
                var bossHelper = BossReflect.GetMember(missileBase, "bossFightHelper");
                if (missilePrefab == null || bossHelper == null) return;

                if (!_d2Resolved)
                {
                    _d2Resolved = true;
                    _rocketType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.DesertMissileRocket");
                    _autoPoolType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.AutoPool");
                    if (_rocketType != null)
                        _rocketInit = _rocketType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "Initialize" && m.GetParameters().Length == 5);
                    if (_autoPoolType != null)
                    {
                        // GetInstance is overloaded (GameObject/string + generic variants) — take the non-generic
                        // GameObject one. It returns an AutoPooledObject (a Component), whose .gameObject is the instance.
                        _autoPoolGetInstance = _autoPoolType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "GetInstance" && !m.IsGenericMethodDefinition
                                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(GameObject));
                        _autoPoolReleaseGO = _autoPoolType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "ReleaseInstance"
                                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(GameObject));
                    }
                    _psType = HarmonyLib.AccessTools.TypeByName("UnityEngine.ParticleSystem");
                    if (_psType != null) _psPlay = HarmonyLib.AccessTools.Method(_psType, "Play", Type.EmptyTypes);
                }
                if (_rocketType == null || _rocketInit == null || _autoPoolGetInstance == null || _autoPoolType == null) return;
                if (_autoPool == null || !_autoPool) _autoPool = UnityEngine.Object.FindAnyObjectByType(_autoPoolType);
                if (_autoPool == null) return;
                if (_ghostDummyMarker == null)
                {
                    _ghostDummyMarker = new GameObject("CoopGhostRocketMarker");
                    UnityEngine.Object.DontDestroyOnLoad(_ghostDummyMarker);
                }

                float lifetime = BossReflect.GetMember(missileBase, "missileLifetime") is float lf ? lf : 15f;
                float startY = BossReflect.GetMember(missileBase, "missileStartYOffset") is float sy ? sy : 30f;
                float yHit = BossReflect.GetMember(missileBase, "yPosWorldToHit") is float yh ? yh : -5.2f;

                int others = 0, spawned = 0;
                NetGameplaySyncBridge.ForEachRemotePlayerTransform((peerId, proxyT) =>
                {
                    others++;
                    try
                    {
                        var pooled = _autoPoolGetInstance.Invoke(_autoPool, new object[] { missilePrefab }) as Component;
                        if (pooled == null) return;
                        var rocketGO = pooled.gameObject;
                        var rocket = rocketGO.GetComponent(_rocketType);
                        if (rocket == null) return;
                        Vector3 start = proxyT.position; start.y = startY;
                        rocketGO.transform.SetPositionAndRotation(start, Quaternion.LookRotation(Vector3.down));
                        try { _rocketType.GetField("yWorldPosToHit")?.SetValue(rocket, yHit); } catch { }
                        try { _rocketType.GetField("isPatternRocket")?.SetValue(rocket, false); } catch { }
                        // The ground target marker, exactly like the native spawn (pool-get, under the rocket, at the
                        // impact height, play). Released back to the pool when the ghost explodes (OnRocketDestroyed →
                        // ReleaseGhostMarker); the inert dummy is only the fallback so Initialize never sees null.
                        GameObject marker = _ghostDummyMarker!;
                        if (markerPrefab != null && _autoPoolGetInstance.Invoke(_autoPool, new object[] { markerPrefab }) is Component pooledMarker && pooledMarker != null)
                        {
                            marker = pooledMarker.gameObject;
                            marker.transform.SetParent(rocketGO.transform, worldPositionStays: true);
                            marker.transform.position = new Vector3(start.x, yHit + 0.1f, start.z);
                            marker.transform.rotation = Quaternion.identity;
                            if (_psType != null) { var ps = marker.GetComponent(_psType); if (ps != null) { try { _psPlay?.Invoke(ps, null); } catch { } } }
                        }
                        NetBossEncounterManager.RegisterGhostRocket((UnityEngine.Object)rocket, ReferenceEquals(marker, _ghostDummyMarker) ? null : marker);
                        _rocketInit.Invoke(rocket, new object[] { bossHelper, proxyT, lifetime, marker, false });
                        spawned++;
                    }
                    catch (Exception exIn) { Plugin.Log.Warn($"[DesertMissile] ghost rocket spawn failed for {peerId}: {exIn.GetType().Name}: {exIn.InnerException?.Message ?? exIn.Message}"); }
                });

                if (Plugin.Cfg.LogBossPreFight.Value && UnityEngine.Time.realtimeSinceStartup >= _ghostRocketLogNext)
                { _ghostRocketLogNext = UnityEngine.Time.realtimeSinceStartup + 1f; Plugin.Log.Info($"[DesertMissile] ghost visual rockets: proxies={others} spawned={spawned}"); }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DesertMissile] SpawnGhostVisualRockets failed: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        /// <summary>F4-MISSILE D2: a ghost rocket exploded — return its ground marker to the AutoPool (mirrors the native
        /// PoolHitMarker; the gradient reset is skipped since a ghost's marker never gets the fired-color change).</summary>
        internal static void ReleaseGhostMarker(GameObject marker)
        {
            try
            {
                if (marker == null || _autoPoolReleaseGO == null) return;
                if (_autoPool == null || !_autoPool) return;
                _autoPoolReleaseGO.Invoke(_autoPool, new object[] { marker });
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DesertMissile] ReleaseGhostMarker failed: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        // LD-Sandstorm / F4 (arena-movement sync): move the whole perimeter so its danger-zone sphere lands at `center`.
        // The native fight moves `desertClausePerimeter.gameObject.transform` (decomp UpdatePerimeterMovement) and the sphere
        // collider + jump points + danger-zone shader (`UpdateDangerZone` reads sphereCollider.transform.position) are its
        // children, so delta-moving the root carries everything. Only x/z move (native keeps y); on the client the perimeter
        // never moves on its own (its phase logic is suppressed), so there is nothing fighting this write.
        public override bool TrySetArenaCenter(object component, Vector3 center)
        {
            if (!BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return false; // DesertClause only
            var perimeter = BossReflect.GetMember(component, "desertClausePerimeter");
            if (!(perimeter is Component pc) || pc == null) return false;
            Vector3 cur = pc.transform.position;
            var sphere = BossReflect.GetMember(perimeter, "sphereCollider");
            if (sphere is Component sc && sc != null) cur = sc.transform.position; // the streamed centre is the sphere's pos
            Vector3 delta = center - cur; delta.y = 0f;
            if (delta.sqrMagnitude < 0.0001f) return true;
            pc.transform.position += delta;
            return true;
        }

        // LD-Sandstorm / F4: DesertClause is a composite boss whose visible body is assembled by its own local intro
        // chain (OnStartInteractWithBoss → DelayIntro → "IntroStarted" anim → animation event → TriggerFight, which hides
        // sandSantaAnimationSprite + sets "BossStarted"). It must run that intro locally to become visible, so it is kept
        // out of the generic puppet system for the intro's duration. Terrorbaum/Lucia appear fully-formed → false.
        public override bool RunsLocalIntroPresentation(object component)
            => BossReflect.HasMethod(component, "OnStartInteractWithBoss");

        // LD-Sandstorm / F4 combat-entry sync: Desert enters real combat through TriggerFight (an animation-event on the
        // intro clip: base.TriggerFight sets fightStarted + StartBossPhases). On a client whose local intro animation
        // never reached that event (out-of-arena / animator culled — Log298 host-first), fightStarted stays false, the
        // boss is frozen out of the puppet system, and no combat begins. The host broadcasts its own TriggerFight and the
        // client applies it here (idempotent). Only Desert (RunsLocalIntroPresentation); generic helpers start via the
        // normal encounter-start path, so they never see a "second" combat-entry event.
        public override bool IsCombatEntrySource(object component, string source)
            => ParseSourceMethod(source) == "TriggerFight" && RunsLocalIntroPresentation(component);

        public override bool TryApplyCombatEntry(object component, out string detail)
        {
            if (IsStarted(component)) { detail = "already-started"; return true; }
            bool ok = BossReflect.TryInvoke(component, "TriggerFight", out detail);
            // The client's local intro applied OnStartInteractWithBoss, which took the Cinematic controller-lock +
            // invulnerability (decomp 4814/4815). Natively they are released at the END of the StartSandstorm coroutine —
            // which we are skipping (we jump straight to combat). base.TriggerFight does NOT release them, so release them
            // here (mirrors StartSandstorm's tail) or the local player stays frozen + invulnerable forever.
            string lockDetail = "no-op";
            try
            {
                var gm = GameManager.Instance;
                if (gm != null)
                {
                    gm.ModifyPlayerInvulnerability(LockStatePadlock.Cinematic, state: false);
                    gm.ModifyControllerLock(LockStatePadlock.Cinematic, state: false);
                    lockDetail = "released Cinematic lock+invuln";
                }
            }
            catch (Exception ex) { lockDetail = $"lock-release ex {ex.GetType().Name}"; }
            detail = $"TriggerFight: {detail}; {lockDetail}";
            return ok;
        }

        // ---- LD-Sandstorm / F4 Stage 2: mid-fight dialog sync (Desert airstrike / sniper / terminator) ----
        // The Desert boss opens mid-fight dialogs by setting bossNPC.dialog.graph to one of these fields then calling
        // bossNPC.Interact(null). On the client the boss is passive (UpdatePhases suppressed), so it never opens them.
        // The host detects the open (Npc.Interact postfix → OnHostBossDialogInteract), identifies the graph here, and
        // broadcasts a "Dialog:<id>" discrete event; the client sets the same graph + Interact to play the same cutscene.
        private static readonly string[] DesertDialogFields = { "airstrikeDialogue", "sniperDialogue", "terminatorDialogue" };
        private static readonly string[] DesertDialogIds    = { "airstrike", "sniper", "terminator" };

        private static object? GetBossNpc(object component)
            => BossReflect.GetMember(component, "bossNPC") ?? BossReflect.GetMember(component, "bossUnit");

        // Name of a DialogueTree graph (UnityEngine.Object), with any runtime "(Clone)" suffix stripped, for by-name
        // matching (the bound runtime graph instance is not reference-equal to the assigned template field).
        private static string GraphName(object? graph)
            => (graph is UnityEngine.Object uo && uo != null) ? uo.name.Replace("(Clone)", "").Trim() : "";

        // TB-D: sentinel id for a generic boss's own opening dialog (Terrorbaum). There is a single dialog on bossNPC,
        // so no field-array is needed — the client mirrors by opening the boss's default dialog (Interact(null)).
        private const string BossOwnDialogId = "boss-open";

        /// <summary>HOST: if the boss's NPC currently has an active dialog graph that should be broadcast + mirrored,
        /// return its id — a Desert mid-fight id (airstrike/sniper/terminator) or the generic <see cref="BossOwnDialogId"/>
        /// for a <see cref="BroadcastsBossOwnDialog"/> boss (Terrorbaum's opening dialogue).</summary>
        public override bool TryGetActiveMidFightDialogId(object component, out string dialogId)
        {
            dialogId = "";
            try
            {
                // TB-D: a generic bossNPC.dialog boss (Terrorbaum) — its single opening dialog is host-broadcast + mirrored.
                // Return the sentinel up-front: it must NOT depend on the live dialog graph (the host may broadcast the open
                // WITHOUT opening its own copy — e.g. when it's still out of arena — so the graph isn't loaded yet). Desert is
                // excluded (BroadcastsBossOwnDialog is false for it), so this can't shadow the Desert mid-fight resolution.
                if (BroadcastsBossOwnDialog(component)) { dialogId = BossOwnDialogId; return true; }
                var npc = GetBossNpc(component);
                var dialog = npc == null ? null : BossReflect.GetMember(npc, "dialog");
                var graph = dialog == null ? null : BossReflect.GetMember(dialog, "graph");
                // Compare by NAME, not reference: the NodeCanvas dialog controller's `graph` getter returns a bound
                // runtime instance, not the assigned template field, so ReferenceEquals(airstrikeDialogue, graph) always
                // fails (Log290: graph name was "Dialog_DesertClauseAirstrike" yet the reference match reported <none>).
                string gName = GraphName(graph);
                if (string.IsNullOrEmpty(gName)) return false;
                if (BossReflect.HasMethod(component, "OnStartInteractWithBoss")) // DesertClause mid-fight dialogs
                {
                    for (int i = 0; i < DesertDialogFields.Length; i++)
                        if (GraphName(BossReflect.GetMember(component, DesertDialogFields[i])) == gName) { dialogId = DesertDialogIds[i]; return true; }
                    // NOTE: the pre-fight INTRO cutscene ("Dialog_DesertClauseIntro") is NOT synced here. Both ends run the
                    // real intro chain (OnStartInteractWithBoss), so each opens it locally — broadcasting would double-open.
                    return false;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>TB-D: Terrorbaum's opening dialogue is the boss's own <c>bossNPC.dialog</c> (opened via
        /// <c>Npc.Interact</c>), driven host-authoritatively and mirrored (not each-end-local like Desert's intro).
        /// Scoped to verified bosses; Desert is excluded (it runs its intro cutscene locally on every end).</summary>
        public override bool BroadcastsBossOwnDialog(object component)
        {
            try
            {
                if (component == null || BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return false;
                return component.GetType().Name == "TerrorbaumBossFightHelper";
            }
            catch { return false; }
        }

        /// <summary>TB: Terrorbaum is a straightforward CombatEnemy-puppet boss with no bespoke client-side sync (unlike
        /// Desert's composite intro or the Witch/Emperor phase machinery), so its client copy is a PURE PUPPET — the host
        /// runs all mechanics and the client only mirrors. Scoped to Terrorbaum; Desert (OnStartInteractWithBoss) and any
        /// other specially-synced boss are excluded so their existing client-side logic is not regressed.</summary>
        public override bool ClientBossIsPurePuppet(object component)
        {
            try
            {
                if (component == null || BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return false;
                return component.GetType().Name == "TerrorbaumBossFightHelper";
            }
            catch { return false; }
        }

        /// <summary>TB: CLIENT — disable the boss's two local mechanic drivers so it never runs its own attacks/phases:
        /// the fight helper itself (its <c>Update</c>/<c>FixedUpdate</c> tick <c>UpdatePhases</c>, enabled by TriggerFight's
        /// <c>base.enabled=true</c>) and its <c>bossPhases</c> (own <c>Update</c> drives phase transitions, enabled by
        /// <c>StartBossPhases</c>). Returns the number newly disabled (0 = already inert), so the caller logs only on the
        /// transition.</summary>
        public override int SuppressClientMechanics(object component, out string detail)
        {
            detail = "";
            try
            {
                int n = 0;
                if (component is Behaviour helper && helper != null && helper.enabled) { helper.enabled = false; n++; }
                var phases = BossReflect.GetMember(component, "bossPhases");
                if (phases is Behaviour bp && bp != null && bp.enabled) { bp.enabled = false; n++; }
                // TB-DMG (Log359): StartBossPhases sets bossPhases.isTransitioning=true and only BossPhases.Update
                // (which we just disabled) ever clears it — so the client's IsTransitioning stuck TRUE forever, which
                // permanently closed the native OnEyeHit gate (!IsTransitioning) → the eye window never opened → zero
                // hits were ever routed (host hitRecv=0). Clear it whenever the driver is suppressed: on this end the
                // flag is only read by OnEyeHit, and the HOST re-checks its own live IsTransitioning on apply anyway.
                if (phases != null && BossReflect.TryGetBool(phases, "isTransitioning", out bool trans) && trans)
                { if (TrySetMember(phases, "isTransitioning", false)) n++; }
                detail = $"disabled {n} mechanic driver(s)/flag(s) (helper+bossPhases+isTransitioning)";
                return n;
            }
            catch (Exception ex) { detail = $"ex {ex.GetType().Name}: {ex.Message}"; return 0; }
        }

        private static bool IsTerrorbaum(object component)
        {
            try { return component != null && component.GetType().Name == "TerrorbaumBossFightHelper"; }
            catch { return false; }
        }

        // ---- TB-DMG: Terrorbaum eye-hit damage authority --------------------------------------------------------
        // The Terrorbaum body is PERMANENTLY invulnerable (SetBossVars → SetInvulnerable(true)); its only damage path
        // is the native OnEyeHit — fired by the projectile dispatch's onHitboxHit AFTER the first (rejected)
        // ReceiveDamage — which requires (fightStarted && !IsTransitioning && part==Eye) and lifts the invulnerability
        // just around a second ReceiveDamage. So on the client, per eye pellet, OUR ReceiveDamage prefix sees TWO
        // calls: pass 1 with isInvulnerable=true (must fall through → vanilla rejects it, zero damage, like a body
        // shot) and pass 2 inside the eye window with isInvulnerable=false (the real damage → route to the host as
        // role "eye"). The host apply then replicates the same window around its own ReceiveDamage (Log358: every
        /// client hit was routed as "main" and rejected by the host's standing invulnerability — hp frozen).
        public override string? ResolveHitTargetRole(object component, object hitUnit)
        {
            if (!IsTerrorbaum(component)) return base.ResolveHitTargetRole(component, hitUnit);
            if (hitUnit == null) return null;
            var hu = GetHealthUnit(component);
            if (hu == null || !ReferenceEquals(hu, hitUnit)) return null;
            // Eye window → route to the host. Outside it ("body", pass 1 of every pellet incl. eye pellets) → the
            // sentinel role: the manager swallows it locally with NO request (vanilla would reject it against the
            // standing invulnerability anyway; letting it fall through would hand it to the ordinary roster hit path,
            // which would spam the host with per-pellet requests the host must reject).
            return BossReflect.TryGetBool(hitUnit, "isInvulnerable", out bool inv) && !inv ? "eye" : "body";
        }

        public override object? ResolveHostTargetForRole(object component, string role)
            => role == "eye" && IsTerrorbaum(component) ? GetHealthUnit(component) : base.ResolveHostTargetForRole(component, role);

        /// <summary>TB-DMG: HOST — apply a routed eye hit exactly like the native OnEyeHit: gate on
        /// (fightStarted &amp;&amp; !IsTransitioning), lift the standing invulnerability around ReceiveDamage, restore it.</summary>
        public override bool TryApplyHostBossHit(object component, string role, object target, float damage, int damageTypeInt, object? source, out bool vanillaResult, out string detail)
        {
            if (role != "eye" || !IsTerrorbaum(component))
                return base.TryApplyHostBossHit(component, role, target, damage, damageTypeInt, source, out vanillaResult, out detail);
            vanillaResult = false;
            if (!IsStarted(component)) { detail = "eye-gate: fight not started"; return false; }
            if (BossReflect.TryGetBool(component, "IsTransitioning", out bool trans) && trans) { detail = "eye-gate: transitioning"; return false; }
            BossReflect.TryInvokeBool(target, "SetInvulnerable", false, out _);
            try
            {
                bool ok = base.TryApplyHostBossHit(component, role, target, damage, damageTypeInt, source, out vanillaResult, out detail);
                detail = "eye-window: " + detail;
                return ok;
            }
            finally { BossReflect.TryInvokeBool(target, "SetInvulnerable", true, out _); }
        }

        /// <summary>TB-D: HOST — open the boss's own opening dialog (bossNPC.Interact(null)). The native Interact postfix
        /// then runs OnHostBossDialogInteract → broadcasts "Dialog:&lt;id&gt;" so every client mirrors it.</summary>
        public override bool TryOpenBossOwnDialog(object component, out string detail)
        {
            detail = "";
            try
            {
                if (!BroadcastsBossOwnDialog(component)) { detail = "not-a-bossOwnDialog-boss"; return false; }
                var bnpc = GetBossNpc(component);
                if (bnpc == null) { detail = "no bossNPC"; return false; }
                var interact = FindInteract(bnpc.GetType());
                if (interact == null) { detail = "Interact(1-arg) not found"; return false; }
                interact.Invoke(bnpc, new object?[] { null });
                detail = "opened boss-own dialog";
                return true;
            }
            catch (Exception ex) { detail = $"ex {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        /// <summary>CLIENT: apply a host "Dialog:&lt;id&gt;" event — set the same dialog graph + open it locally.</summary>
        public override bool TryApplyDiscreteEvent(object component, string eventName, bool hasPos, Vector3 pos, out string detail)
        {
            detail = "";
            // CLIENT (LD-Sandstorm / F4 Stage 3, phase-action sync): the host boss dismounted its pike. Since the boss is
            // now a host-driven puppet (classified CombatEnemy), its POSITION is owned by the puppet system — it follows
            // the host boss down to the ground (real jump arc) on its own. We must NOT also drive the position manually:
            // a hand-rolled descent fights the puppet drive and flings the boss ~130 m away (Log290 maxErr=130, boss
            // "disappeared"). So here we only: (1) sever the boss from the local pike's attachedUnits + reparent off the
            // mount, because the pike's Update() zeroes every attached unit's localPosition each frame and would fight the
            // puppet drive; (2) re-enable the animator (the pike disabled it on attach); (3) toggle "JumpingOffPike" so the
            // jump-off animation plays (the generic animation mirror carries state hashes, not this bool parameter).
            if (eventName == "BossJump" || eventName == "BossLand")
            {
                bool jumping = eventName == "BossJump";
                var npc = GetBossNpc(component);
                if (!(npc is Component bodyC) || bodyC == null) { detail = "no boss body"; return false; }
                var animator = BossReflect.GetMember(npc, "animator") as Animator;
                SeverBossFromPike(component, npc);
                DetachBossFromMount(npc, bodyC.transform, animator);
                if (!jumping) TrySetMember(npc, "isAttachedToUnit", false);
                if (animator != null) animator.SetBool("JumpingOffPike", jumping);
                detail = jumping ? "detached mount + JumpingOffPike=true (puppet owns position)"
                                 : "JumpingOffPike=false (puppet owns position)";
                return true;
            }
            // CLIENT (TB-ANIM): Terrorbaum visibility-state mirror. The client boss is a pure puppet (mechanics
            // suppressed), so WITHOUT these its tree never digs/erupts — one end shows a standing tree while the other
            // shows nothing (Log358: host underground+invisible, client standing). The host broadcasts its native
            // dig/erupt/aoe/root state changes; the client replays the same native mutators (animator bools/triggers +
            // flags + the erupt reposition). Damage stays host-authoritative: the anim-damage events these animations
            // fire are blocked client-side (BossEncounterPatches), and host slam/erupt damage reaches remote players
            // through the ghost-proxy damage forward.
            if (IsTerrorbaum(component))
            {
                if (eventName == "TerrorDig") { bool ok = BossReflect.TryInvoke(component, "StartDigging", out string dd); detail = $"dig {dd}"; return ok; }
                if (eventName == "TerrorEruptAoe") { bool ok = BossReflect.TryInvoke(component, "OnEruptStartAoe", out string ad); detail = $"erupt-aoe {ad}"; return ok; }
                if (eventName == "TerrorRoot") { bool ok = BossReflect.TryInvoke(component, "OnRootStart", out string rd); detail = $"root {rd}"; return ok; }
                if (eventName != null && eventName.StartsWith("TerrorErupt:", StringComparison.Ordinal))
                {
                    string trigger = eventName.Substring("TerrorErupt:".Length);
                    if (!hasPos) { detail = "erupt without position"; return false; }
                    try
                    {
                        var m = component.GetType().GetMethod("OnStartEruptAttack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m == null) { detail = "OnStartEruptAttack not found"; return false; }
                        m.Invoke(component, new object[] { pos, trigger });
                        detail = $"erupt at {pos:F1} trigger={trigger}";
                        return true;
                    }
                    catch (Exception ex) { detail = $"erupt ex {ex.GetType().Name}: {ex.Message}"; return false; }
                }
            }
            // CLIENT: the host's boss dialog closed → finalize our copy (the client's dialog won't end on its own,
            // because the boss actions it waits on are suppressed here). Real Graph.Stop(true) via BossDialogReflect.
            if (eventName == "DialogClose")
            {
                bool closed = BossDialogReflect.TryFinalizeCurrentDialog(out string cd);
                detail = $"dialog close finalized={closed} ({cd})";
                return true;
            }
            // CLIENT (LD-Sandstorm / F4): the host played the sandstorm presentation → play it here too. Invoke the real
            // Anim_OnTriggerSandstorm (→ StartSandstorm: arena-edge storm + music + fog, releases the Cinematic lock at its
            // tail). Its prefix (OnLocalBossSandstorm) dedups per key, so if this end already played the sandstorm via its
            // own local intro the invoke is a harmless no-op (StartSandstorm is skipped).
            if (eventName == "Sandstorm")
            {
                bool ok = BossReflect.TryInvoke(component, "Anim_OnTriggerSandstorm", out string sd);
                detail = $"sandstorm {sd}";
                return ok;
            }
            if (eventName == null || !eventName.StartsWith("Dialog:", StringComparison.Ordinal)) { detail = "not-a-dialog-event"; return false; }
            string id = eventName.Substring(7);
            // TB-D: Terrorbaum's opening dialogue — the boss's own dialog graph is already the opening one, so just open it.
            if (id == BossOwnDialogId)
            {
                var bnpc = GetBossNpc(component);
                if (bnpc == null) { detail = "no bossNPC"; return false; }
                var bInteract = FindInteract(bnpc.GetType());
                if (bInteract == null) { detail = "Interact(1-arg) not found"; return false; }
                bInteract.Invoke(bnpc, new object?[] { null });
                detail = "opened boss-own dialog";
                return true;
            }
            int idx = Array.IndexOf(DesertDialogIds, id);
            if (idx < 0) { detail = "unknown-dialog:" + id; return false; }
            try
            {
                var npc = GetBossNpc(component);
                var dialog = npc == null ? null : BossReflect.GetMember(npc, "dialog");
                var graph = BossReflect.GetMember(component, DesertDialogFields[idx]);
                if (npc == null || dialog == null || graph == null) { detail = $"missing npc/dialog/graph (npc={npc != null} dialog={dialog != null} graph={graph != null})"; return false; }
                if (!TrySetMember(dialog, "graph", graph)) { detail = "set-graph-failed"; return false; }
                var interact = FindInteract(npc.GetType());
                if (interact == null) { detail = "Interact(1-arg) not found"; return false; }
                interact.Invoke(npc, new object?[] { null });
                detail = $"opened dialog {id}";
                return true;
            }
            catch (Exception ex) { detail = $"ex {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        private static MethodInfo? FindInteract(Type npcType)
        {
            for (Type? t = npcType; t != null; t = t.BaseType)
            {
                var m = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .FirstOrDefault(mi => mi.Name == "Interact" && mi.GetParameters().Length == 1);
                if (m != null) return m;
            }
            return null;
        }

        private static bool TrySetMember(object obj, string name, object value)
        {
            for (Type? t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(obj, value); return true; }
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite) { p.SetValue(obj, value); return true; }
            }
            return false;
        }

        // LD-Sandstorm / F4 Stage 3: remove the boss from its pike carrier's `attachedUnits` list on the client. The
        // carrier's Update() zeroes every attached unit's localPosition every frame (decomp DesertPikeCarrier.Update), so
        // until the boss is off that list any position we set is stomped back to the mount/origin. spawnedBossPike is the
        // boss's pike Unit; the DesertPikeCarrier is a component on it holding the list. Idempotent.
        private static void SeverBossFromPike(object helper, object bossNpc)
        {
            try
            {
                var pike = BossReflect.GetMember(helper, "spawnedBossPike");
                if (!(pike is Component pikeC) || pikeC == null) return;
                foreach (var comp in pikeC.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var f = comp.GetType().GetField("attachedUnits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null) continue;
                    if (f.GetValue(comp) is IList list && list.Contains(bossNpc))
                    {
                        list.Remove(bossNpc);
                        Plugin.Log.Info("[BossPhaseAction] severed boss from pike attachedUnits (client)");
                    }
                    return;
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossPhaseAction] SeverBossFromPike failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // LD-Sandstorm / F4 Stage 3: replay the native pike-carrier detach on the client boss so it can leave the mount.
        // The native attach parents the body to a mount point + disables its animator; detach reparents to unitRoot,
        // re-enables the animator, unflips the sprite scale and re-enables billboard rotation. Idempotent (safe to call
        // for both BossJump and BossLand).
        private static void DetachBossFromMount(object npc, Transform body, Animator? animator)
        {
            try
            {
                Transform? root = null;
                try { root = GameManager.Instance != null ? GameManager.Instance.unitRoot : null; } catch { }
                if (body.parent != root) body.SetParent(root, worldPositionStays: true);
                var s = body.localScale; s.x = Mathf.Abs(s.x); body.localScale = s;
                if (animator != null) animator.enabled = true;
                var billboard = BossReflect.GetMember(npc, "billboard");
                if (billboard != null) TrySetMember(billboard, "disableBillboardRotation", false);
            }
            catch { }
        }

        // DesertClause: OnStartInteractWithBoss() (player interact) -> DelayIntro coroutine -> anim event ->
        // TriggerFight() (sets fightStarted). Generic helpers (Terrorbaum/Lucia) only expose TriggerFight; the base
        // ResolveApplyMethod falls back to it when OnStartInteractWithBoss is absent on the type.
        public override string[] StartChainMethods => new[] { "OnStartInteractWithBoss", "TriggerFight" };

        // Phase 5.4-F2: on the CLIENT, do NOT replay OnStartInteractWithBoss. LogOutput28 showed two Desert problems
        // caused by it: (1) it adds a Cinematic controller lock that is only released at the END of the intro->
        // sandstorm animation chain (StartSandstorm), which doesn't complete client-side -> camera stays locked;
        // (2) its DelayIntro does RepositionBossFromCamera(...) based on the CLIENT camera -> the boss is moved off
        // its mount/placed position (~3u offset). Invoking TriggerFight() directly starts the fight without the
        // cinematic lock and without the client-camera reposition, so the boss stays on its real position. The Host
        // still replays the full intro for itself (this override is client-only).
        public override bool TryApplyHostStart(object component, NetBossEncounterState state, out string detail)
        {
            bool shortcut = false; try { shortcut = Plugin.Cfg.EnableBossClientPresentation.Value; } catch { }
            if (shortcut
                && NetGameplaySyncBridge.BossMode == NetMode.Client
                && BossReflect.HasMethod(component, "OnStartInteractWithBoss")
                && BossReflect.HasMethod(component, "TriggerFight"))
            {
                if (IsStarted(component)) { detail = "already-started"; return true; }
                bool ok = BossReflect.TryInvoke(component, "TriggerFight", out detail);
                detail = $"client-presentation-safe TriggerFight (skipped OnStartInteractWithBoss cinematic): {detail}";
                return ok;
            }
            return base.TryApplyHostStart(component, state, out detail);
        }

        private Renderer? _pikeVisual;
        private Renderer? _pikeVisualSource;
        private bool _pikeVisualLogged;

        // LD-Sandstorm / F4 (pike-riding visibility): cosmetic, BOTH ends. The real boss body's "Sprite" renderer flakily
        // gets stuck enabled=false in co-op (native JumpTowards re-enable lost — Log311 showed the client hitting it too,
        // not just the host) and CANNOT be forced on without fighting the burrow cycle. So instead of touching the real
        // renderer, mirror it onto an OWNED renderer that native never manages: a clone parented under the real Sprite's
        // transform, mirroring its visual state each frame (the animator keeps driving the disabled renderer's data). The
        // clone rides the pike and burrows with it (its parent GameObject toggles active). Only enabled while the real
        // Sprite renderer is OFF — on an end where native works the clone stays disabled. Type-agnostic (Log312: the
        // SpriteRenderer-only search silently never matched — the body renderer may be a MeshRenderer quad): a
        // SpriteRenderer source mirrors sprite/flip/color; any other Renderer type mirrors mesh + materials + property
        // block. Logs what it does so a silent no-op can't recur.
        public void EnsureBossPikeVisual(object component)
        {
            try
            {
                var npc = GetBossNpc(component);
                if (!(npc is Component bc) || bc == null) return;
                Renderer? real = _pikeVisualSource;
                if (real == null)
                {
                    foreach (var r in bc.GetComponentsInChildren<Renderer>(true))
                        if (r != null && r.gameObject.name == "Sprite") { real = r; break; }
                    if (real == null)
                    {
                        if (!_pikeVisualLogged) { _pikeVisualLogged = true; Plugin.Log.Warn("[BossPhaseAction] pike visual: no renderer named 'Sprite' on the boss body"); }
                        return;
                    }
                    _pikeVisualSource = real;
                }
                if (real.enabled) { if (_pikeVisual != null && _pikeVisual.enabled) _pikeVisual.enabled = false; return; }
                if (_pikeVisual == null)
                {
                    var go = new GameObject("CoopBossPikeVisual");
                    go.transform.SetParent(real.transform, worldPositionStays: false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    if (real is SpriteRenderer)
                    {
                        _pikeVisual = go.AddComponent<SpriteRenderer>();
                    }
                    else
                    {
                        // Generic path (e.g. MeshRenderer quad): mirror the mesh + materials.
                        var srcFilter = real.GetComponent<MeshFilter>();
                        if (srcFilter != null) go.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;
                        _pikeVisual = (Renderer)go.AddComponent(real.GetType());
                    }
                    Plugin.Log.Info($"[BossPhaseAction] pike visual clone created (source type={real.GetType().Name})");
                }
                var c = _pikeVisual;
                if (real is SpriteRenderer sr && c is SpriteRenderer csr)
                {
                    csr.sprite = sr.sprite;
                    csr.flipX = sr.flipX; csr.flipY = sr.flipY;
                    csr.color = sr.color;
                }
                else
                {
                    var srcFilter = real.GetComponent<MeshFilter>();
                    var dstFilter = c.GetComponent<MeshFilter>();
                    if (srcFilter != null && dstFilter != null && dstFilter.sharedMesh != srcFilter.sharedMesh)
                        dstFilter.sharedMesh = srcFilter.sharedMesh;
                    var mpb = new MaterialPropertyBlock();
                    real.GetPropertyBlock(mpb);
                    c.SetPropertyBlock(mpb);
                }
                c.sharedMaterials = real.sharedMaterials;
                c.sortingLayerID = real.sortingLayerID;
                c.sortingOrder = real.sortingOrder;
                c.enabled = true;
            }
            catch (Exception ex)
            {
                if (!_pikeVisualLogged) { _pikeVisualLogged = true; Plugin.Log.Warn($"[BossPhaseAction] pike visual failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // LD-Sandstorm / F4 (pike-riding visibility probe): dump the Desert boss body's actual render + transform state so
        // we can see WHY it is invisible in co-op though SP shows it riding the pike persistently — is it (a) positioned
        // underground (y), (b) its GameObject inactive, (c) its renderers disabled, or (d) parented off/on the mount?
        public void LogDesertVisibility(object component, NetMode mode)
        {
            try
            {
                var npc = GetBossNpc(component);
                if (!(npc is Component bc) || bc == null) { Plugin.Log.Info($"[DesertVis] {mode} no bossNPC"); return; }
                var body = bc.transform;
                var rends = bc.GetComponentsInChildren<Renderer>(true);
                // Per-renderer: name=state where state = on(enabled+active) / OFF-rend / OFF-go, plus /vis (Renderer.isVisible
                // = actually drawn by a camera this frame) — distinguishes "enabled but culled/blank" from "truly drawing".
                var sb = new System.Text.StringBuilder();
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    bool en = r.enabled && r.gameObject.activeInHierarchy;
                    sb.Append(r.gameObject.name).Append('=').Append(en ? (r.isVisible ? "on/vis" : "on/CULLED") : (!r.enabled ? "OFF-rend" : "OFF-go")).Append(' ');
                }
                string parent = body.parent != null ? body.parent.name : "<root>";
                var animator = BossReflect.GetMember(npc, "animator") as Animator;
                string anim = animator == null ? "null" : $"en={animator.enabled} spd={animator.speed:F1} scale={bc.transform.lossyScale.x:F2}";
                var bossUnit = BossReflect.GetMember(component, "bossUnit");
                Vector3 unitPos = (bossUnit is Component buc && buc != null) ? buc.transform.position : Vector3.zero;
                // P1 fire diagnosis: is the machine-gun trigger held, and at whom is the AI aiming? On the host this shows
                // the target rotation working; on the client a trigger that never goes true = the host fire mirror not
                // reaching the boss puppet.
                var weapon = BossReflect.GetMember(npc, "weapon");
                bool trig = weapon != null && BossReflect.GetMember(weapon, "bIsTriggerActive") is bool tb && tb;
                var aiAgent = BossReflect.GetMember(npc, "AiAgent") ?? BossReflect.GetMember(npc, "aiAgent");
                var tgt = aiAgent == null ? null : BossReflect.GetMember(aiAgent, "target");
                string tname = tgt == null || (tgt is UnityEngine.Object tuo && tuo == null) ? "null" : ((tgt as Component)?.name ?? tgt.GetType().Name);
                string mirror = SULFURTogether.Networking.Gameplay.NetGameplayProbeManager.DescribeEnemyFireMirrorState(npc);
                Plugin.Log.Info($"[DesertVis] {mode} pos={body.position:F1} activeHier={bc.gameObject.activeInHierarchy} parent={parent} bossUnitPos={unitPos:F1} anim[{anim}] fire[trig={trig} aim={tname}] mirror[{mirror}] rends[{sb.ToString().TrimEnd()}]");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DesertVis] failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // F3 diagnostic for DesertClause (composite boss). LogOutput28/29: replaying OnStartInteractWithBoss keeps the
        // old man VISIBLE but leaves the Cinematic controller lock stuck (it is released ONLY at the end of the
        // StartSandstorm() coroutine, which the Client's intro animation chain doesn't reach). Skipping the intro made
        // him invisible (regression). The real fix (next phase) is to let the Client run the intro chain + a Desert-
        // specific Cinematic cleanup. This logs the boss position/visibility so the next test can confirm.
        public override void OnClientPresentationStart(object component)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (!BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return; // DesertClause only
                if (!Plugin.Cfg.LogBossEncounter.Value) return;
                var bossUnit = BossReflect.GetMember(component, "bossUnit");
                Vector3 pos = (bossUnit is Component bc && bc != null) ? bc.transform.position : Vector3.zero;
                bool active = bossUnit is Component ac && ac != null && ac.gameObject.activeInHierarchy;
                bool started = IsStarted(component);
                Plugin.Log.Info($"[BossPresentation] Desert client state: bossUnit={(bossUnit != null ? "yes" : "no")} active={active} fightStarted={started} pos={pos:F1} — Cinematic lock is released only at StartSandstorm() end; client intro chain must reach it (F4).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossPresentation] Desert failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public override string DescribeForLog(object component)
        {
            bool started = IsStarted(component);
            int phase = -1; bool hasPhase = false;
            var phases = BossReflect.GetMember(component, "bossPhases");
            if (phases != null) hasPhase = BossReflect.TryGetInt(phases, "currentPhaseIndex", out phase);
            var bossUnit = BossReflect.GetMember(component, "bossUnit");
            BossReflect.TryGetBool(component, "canManuallyTransition", out bool canManual);
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} fightStarted={started} phase={(hasPhase ? phase.ToString() : "?")} bossUnit={(bossUnit != null ? "yes" : "no")} canManualTransition={canManual}";
        }

        public override void TryReadState(object component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos)
        {
            phaseIndex = 0; hasPhase = false;
            var phases = BossReflect.GetMember(component, "bossPhases");
            if (phases != null) hasPhase = BossReflect.TryGetInt(phases, "currentPhaseIndex", out phaseIndex);
            hasPos = BossReflect.TryGetPosition(component, out pos);
        }

        // ---- Phase 5.4-E4: generalize dialog-commit to interact/dialog-gated BossFightHelpers ----
        // DesertClause enters via OnStartInteractWithBoss() (player interact -> DelayIntro -> anim event -> TriggerFight).
        // Generic helpers (Terrorbaum) have no OnStartInteractWithBoss and are NOT dialog bosses.
        public override bool IsDialogBoss(object component) => BossReflect.HasMethod(component, "OnStartInteractWithBoss");

        public override bool IsDialogCommitSource(string source)
        {
            var m = ParseSourceMethod(source);
            return m == "OnStartInteractWithBoss" || m == "TriggerFight";
        }

        public override bool ShouldSuppressDuplicateDialogEntry(object component, string source)
        {
            if (!IsStarted(component)) return false;
            // After the fight is started, a re-fired interact would re-open the boss dialog.
            return ParseSourceMethod(source) == "OnStartInteractWithBoss";
        }

        public override bool TryApplyDialogCommit(object component, NetBossDialogCommit commit, out string detail)
        {
            // LD-Sandstorm / F4 (foundation): in faithful mode the boss's native intro chain
            // (OnStartInteractWithBoss → Dialog_DesertClauseIntro → anim-event TriggerFight) is meant to play out on
            // THIS end, so we must NOT finalize/kill the current dialog — that would Stop the very intro cutscene we
            // want the local player to watch. Only the legacy fake-start path (faithful off) finalizes here.
            bool faithful = false; try { faithful = Plugin.Cfg.EnableFaithfulBossIntro.Value; } catch { }
            bool finalized = false; string dlgDetail = "skipped (faithful intro)";
            if (!faithful) finalized = BossDialogReflect.TryFinalizeCurrentDialog(out dlgDetail);
            string startDetail = "already-started";
            if (!IsStarted(component))
            {
                // Run the FULL start entrypoint, not a bare TriggerFight. TriggerFight alone flips fightStarted (health
                // bar) but does NOT run the intro chain that ASSEMBLES the composite Desert body and opens the intro
                // cutscene — so a client-first / commit-driven start showed a health bar with no boss and no dialog
                // (Log294). OnStartInteractWithBoss runs the intro (assembles the body, opens Dialog_DesertClauseIntro →
                // synced, fires TriggerFight from its anim event). The camera reposition is suppressed on both ends
                // (ShouldSuppressClientBossReposition) so the boss stays at its placed arena position. Fall back to
                // TriggerFight for BossFightHelpers without an intro entrypoint (Terrorbaum/Lucia).
                string startMethod = BossReflect.HasMethod(component, "OnStartInteractWithBoss") ? "OnStartInteractWithBoss" : "TriggerFight";
                BossReflect.TryInvoke(component, startMethod, out startDetail);
                startDetail = $"{startMethod}: {startDetail}";
            }
            detail = $"dialogFinalized={finalized} ({dlgDetail}); start={startDetail}";
            return true;
        }
    }
}
