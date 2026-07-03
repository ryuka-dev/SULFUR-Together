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

        /// <summary>HOST: if the boss's NPC currently has one of the mid-fight dialog graphs set, return its id.</summary>
        public override bool TryGetActiveMidFightDialogId(object component, out string dialogId)
        {
            dialogId = "";
            try
            {
                if (!BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return false; // DesertClause only
                var npc = GetBossNpc(component);
                var dialog = npc == null ? null : BossReflect.GetMember(npc, "dialog");
                var graph = dialog == null ? null : BossReflect.GetMember(dialog, "graph");
                // Compare by NAME, not reference: the NodeCanvas dialog controller's `graph` getter returns a bound
                // runtime instance, not the assigned template field, so ReferenceEquals(airstrikeDialogue, graph) always
                // fails (Log290: graph name was "Dialog_DesertClauseAirstrike" yet the reference match reported <none>).
                string gName = GraphName(graph);
                if (string.IsNullOrEmpty(gName)) return false;
                for (int i = 0; i < DesertDialogFields.Length; i++)
                    if (GraphName(BossReflect.GetMember(component, DesertDialogFields[i])) == gName) { dialogId = DesertDialogIds[i]; return true; }
                // NOTE: the pre-fight INTRO cutscene ("Dialog_DesertClauseIntro") is NOT synced here. Both ends run the
                // real intro chain (OnStartInteractWithBoss, see TryApplyDialogCommit), so each opens the intro dialog
                // locally — broadcasting it would double-open on the client. Only the mid-fight calls need syncing (the
                // client is a passive puppet then, with UpdatePhases suppressed, so it never opens them itself).
                return false;
            }
            catch { return false; }
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
                // Per-renderer: name=on/off (on = renderer.enabled AND its GameObject activeInHierarchy = actually drawing).
                var sb = new System.Text.StringBuilder();
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    bool drawing = r.enabled && r.gameObject.activeInHierarchy;
                    sb.Append(r.gameObject.name).Append('=').Append(drawing ? "on" : (!r.enabled ? "OFF-rend" : "OFF-go")).Append(' ');
                }
                string parent = body.parent != null ? body.parent.name : "<root>";
                Vector3 rootPos = body.root != null ? body.root.position : Vector3.zero;
                var bossUnit = BossReflect.GetMember(component, "bossUnit");
                Vector3 unitPos = (bossUnit is Component buc && buc != null) ? buc.transform.position : Vector3.zero;
                Plugin.Log.Info($"[DesertVis] {mode} pos={body.position:F1} activeHier={bc.gameObject.activeInHierarchy} parent={parent} rootPos={rootPos:F1} bossUnitPos={unitPos:F1} rends[{sb.ToString().TrimEnd()}]");
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
