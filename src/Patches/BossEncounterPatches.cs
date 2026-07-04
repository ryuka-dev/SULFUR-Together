using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.4-E: hooks the known Boss start entrypoints and routes them through NetBossEncounterManager.
    /// Discovery-first: each entrypoint is resolved by reflection and logged; missing methods warn (so the
    /// next round can adjust) and never crash. A prefix returning false BLOCKS a joined Client's local start
    /// so the Host owns the authoritative start.
    /// </summary>
    internal static class BossEncounterPatches
    {
        private static SULFURTogether.Logging.STLogger Log => Plugin.Log;

        public static void Apply(Harmony harmony)
        {
            if (!Plugin.Cfg.EnableBossEncounterSync.Value)
            {
                Log.Info("[BossEncounter] Disabled by config.");
                return;
            }

            // A. Generic BossFightHelper family (DesertClause / Terrorbaum / Lucia / ... via the base + overrides).
            PatchStart(harmony, FindType("BossFightHelper",
                "PerfectRandom.Sulfur.Core.BossFightHelper", "PerfectRandom.Sulfur.Gameplay.BossFightHelper"),
                "TriggerFight");

            PatchStart(harmony, FindType("DesertClauseBossFightHelper",
                "PerfectRandom.Sulfur.Core.DesertClauseBossFightHelper", "PerfectRandom.Sulfur.Gameplay.DesertClauseBossFightHelper"),
                "TriggerFight", "OnStartInteractWithBoss");

            // LD-Sandstorm / F4 divergence: on the CLIENT the Desert fight is host-authoritative. Once the boss has
            // assembled (fightStarted, via our TriggerFight allow), suppress its own per-frame phase combat
            // (UpdatePhasesDeltaTime/FixedTime → UpdateAiming → weapon/missile fire) so it does NOT run an independent
            // local fight (the client boss was firing ~6x the host's projectiles + could double-hit the player). Host
            // projectiles replay visually via the projectile sync; health/phase stay host-authoritative (BossState).
            // Desert-scoped (its own override); the intro is unaffected (phases only run after StartBossPhases).
            PatchDesertPhaseSuppression(harmony, FindType("DesertClauseBossFightHelper",
                "PerfectRandom.Sulfur.Core.DesertClauseBossFightHelper", "PerfectRandom.Sulfur.Gameplay.DesertClauseBossFightHelper"));

            // LD-Sandstorm / F4 Stage 3: phase-action / presentation sync. The client boss is a passive puppet whose
            // pike machinery is suppressed, so its dismount (OnBossJump/OnBossLand → the "JumpingOffPike" animator bool)
            // never plays and it stays frozen on the mount. The host broadcasts the dismount as a discrete event; the
            // client replays the animator state (the body's translation already follows via position snapshots).
            PatchDesertDismount(harmony, FindType("DesertClauseBossFightHelper",
                "PerfectRandom.Sulfur.Core.DesertClauseBossFightHelper", "PerfectRandom.Sulfur.Gameplay.DesertClauseBossFightHelper"));

            // LD-Sandstorm / F4 (pike-riding visibility): when the boss mounts its pike (DivingAround), DesertPikeCarrier.
            // AttachUnit hides the boss body (mainRenderer SetActive(false) — it starts burrowed) + parents it to the mount,
            // and the carrier's Update zeroes its localPosition each frame. Native, that's fine (the pike's own jump cycle
            // re-shows it). But the CLIENT boss is a host-driven puppet whose local pike cycle doesn't sync → it stays
            // hidden + drifts to origin (Log303: invisible boss, maxErr 132 m). Postfix AttachUnit (client-only, boss-only)
            // to take the boss off the pike and keep it visible; the puppet position (host boss, which encodes the burrow
            // arc) drives it.
            PatchDesertPikeAttach(harmony, FindType("DesertPikeCarrier",
                "PerfectRandom.Sulfur.Core.DesertPikeCarrier", "PerfectRandom.Sulfur.Gameplay.DesertPikeCarrier"));

            // LD-Sandstorm / F4-MISSILE D1: the boss's homing-missile bases (DesertMissileBase, sniper + terminator) run
            // their own Update, so on the client they fire divergently — at the local player, in phases where the host has
            // already stopped (Log332: client kept firing in phases 4/5). Make the firing WINDOWS host-authoritative: block
            // the client's own StartMissiles and mirror the host's Start/Stop, so the client only fires when the host does.
            PatchDesertMissile(harmony, FindType("DesertMissileBase",
                "PerfectRandom.Sulfur.Gameplay.DesertMissileBase", "PerfectRandom.Sulfur.Core.DesertMissileBase"));

            // F4-MISSILE D2: ghost VISUAL rockets (homing multiplayer). Each real rocket fired at the local player gets
            // damage-suppressed twins homing on the other players' visual proxies; the suppression gate lives on the
            // rocket's own damage pass (a ghost must not double-damage the player already hit by their own real rocket).
            PatchDesertMissileRocket(harmony, FindType("DesertMissileRocket",
                "PerfectRandom.Sulfur.Gameplay.DesertMissileRocket", "PerfectRandom.Sulfur.Core.DesertMissileRocket"));

            // B. Witch standalone system.
            PatchStart(harmony, FindType("WitchBossController",
                "PerfectRandom.Sulfur.Gameplay.WitchBossController", "PerfectRandom.Sulfur.Core.WitchBossController"),
                "EventStarted", "StartFight");

            // C. Cousin standalone system.
            PatchStart(harmony, FindType("CousinHelper",
                "PerfectRandom.Sulfur.Gameplay.CousinHelper", "PerfectRandom.Sulfur.Core.CousinHelper"),
                "Trigger", "TriggerIntro", "Introduction", "StartFight");

            // D. Lucia (Phase 5.4-E3). LuciaBossFightHelper.TriggerFight is `new` (hides base virtual) and is the
            // real start entry; it is reached from the dialog via LuciaBossFightTrigger.TriggerFight().
            PatchStart(harmony, FindType("LuciaBossFightHelper",
                "PerfectRandom.Sulfur.Core.LuciaBossFightHelper"),
                "TriggerFight");

            Log.Info("[BossEncounter] Boss start entrypoint hooks registered.");

            // Phase 5.4-F5: Lucia eye defeat authority. Prefix on EyeDied blocks the joined Client's local eye-death
            // chain (it reports to the Host instead, which consumes one of its own living eyes); postfix lets the Host
            // broadcast the new remaining eye count. Count/cycle authority — no per-eye entity mapping.
            if (Plugin.Cfg.EnableLuciaEyeAuthority.Value)
                PatchLuciaEye(harmony, FindType("LuciaBossFightHelper", "PerfectRandom.Sulfur.Core.LuciaBossFightHelper"));
            else
                Log.Info("[LuciaEye] disabled by config.");

            // Phase 5.4-F6: Lucia terminal death. LuciaBossFightHelper OVERRIDES OnBossDead(Unit) — patch that override
            // specifically (does not affect other BossFightHelper bosses). Prefix isolates host-only loot/save on the
            // client; postfix lets the Host broadcast the terminal death.
            if (Plugin.Cfg.EnableLuciaDeathAuthority.Value)
                PatchLuciaDeath(harmony, FindType("LuciaBossFightHelper", "PerfectRandom.Sulfur.Core.LuciaBossFightHelper"));
            else
                Log.Info("[LuciaDeath] disabled by config.");

            // Phase 5.4-F4: Cousin fixed-point pool events (Submerge / MoveToNewPool / Reappear). Host broadcasts,
            // Client mirrors the SAME pool so both see the cousin dig out of the same hole.
            var cousinType = FindType("CousinHelper",
                "PerfectRandom.Sulfur.Gameplay.CousinHelper", "PerfectRandom.Sulfur.Core.CousinHelper");
            PatchDiscrete(harmony, cousinType, "Submerge", "MoveToNewPool", "Reappear");
            // Phase 5.4-F4 death: postfix-only on CousinDeath (the real terminal). The Host broadcasts; the Client
            // runs its own real death. NOT prefix-blocked (the Client's own CousinDeath, via owner.Die(), must run).
            PatchDeath(harmony, cousinType, "CousinDeath");
            // Phase PF-ArmDefer (issue 1): defer the Cousin intro arm to the dialog-close fight commit. Prefix BLOCKS the
            // behavior-tree intro SpawnArm (which co-op's no-pause lets fire during the dialog); the manager replays it
            // at commit. Gated on DeferBossIntroArm AND the fight gate (the deferral has no meaning without the gate).
            if (Plugin.Cfg.DeferBossIntroArm.Value && Plugin.Cfg.GateBossFightOnDialogClose.Value)
                PatchIntroArm(harmony, cousinType);
            else Log.Info("[BossArmDefer] intro-arm deferral disabled by config.");

            // Phase 5.4-E3: host Witch phase-state broadcast hook.
            PatchWitchPhaseStateHook(harmony);
            // Phase 5.4-G4: WitchPhase2 InitPhase/ShowWitches timing probe (diagnostic) + G5 manifest hooks.
            PatchWitchPhase2Probe(harmony);
            // Phase 5.4-G5: WitchPhase2 Real/Illusion hit result postfixes (host broadcasts hide results).
            if (Plugin.Cfg.EnableWitchPhase2Manifest.Value) PatchWitchP2HitResults(harmony);
            else Log.Info("[WitchP2] manifest disabled by config.");
            // Phase 5.4-G7: Witch death cleanup (amulet crash swallow + terminal mark).
            if (Plugin.Cfg.EnableWitchDeathFix.Value) PatchWitchDeath(harmony);
            else Log.Info("[WitchDeath] death fix disabled by config.");

            // Phase PF-0 (arena lockdown evidence): probe the vanilla room-seal primitives so a real co-op Cousin
            // test reveals whether the boss room uses DoorBlocker/AllDeadTrigger and the host-vs-client seal timing.
            PatchArenaDoorProbe(harmony);

            // Phase PF (FF14 dialog-sync evidence): probe the dialog-open chokepoints (Npc.Interact /
            // DialogController.SetCurrentSpeakable) to confirm whether the Cousin fight starts via a trigger volume
            // (direct boss start, no dialog) or via the boss dialog's Attack option — before wiring the dialog sync.
            PatchDialogFlowProbe(harmony);
            ApplyCutsceneSuppressionPatches(harmony); // RM-2b: no camera/lock/dialog for out-of-room players

            // Phase 5.4-E2: lifecycle probe + Emperor type discovery (diagnostic only).
            ApplyLifecycleProbe(harmony);
            SULFURTogether.Networking.Gameplay.Boss.BossTypeDiscovery.LogEmperorTypes();
            // Phase 5.4-E3: Emperor dual-worm-controller diagnosis + optional client-side suppression scaffold.
            SULFURTogether.Networking.Gameplay.Boss.EmperorWormDiagnostics.Apply(harmony);
            // EMP-6a: Emperor phase-2 SPIDER probe (observe-only; validates the spider sync model before writing it).
            SULFURTogether.Networking.Gameplay.Boss.EmperorSpiderDiagnostics.Apply(harmony);
        }

        /// <summary>Phase 5.4-G2: WitchBossController.ChangePhase is the single phase-transition chokepoint. PREFIX blocks
        /// the Client's own (self-advancing) transitions — the Host owns phase authority and the Client applies by
        /// revision (LogOutput34: the Client drifted ahead and caused wrongRole). POSTFIX (host) broadcasts the new phase
        /// + revision AND the health BossState. Witch phases CYCLE, so revision — not enum magnitude — decides freshness.</summary>
        private static void PatchWitchPhaseStateHook(Harmony harmony)
        {
            var witch = FindType("WitchBossController", "PerfectRandom.Sulfur.Gameplay.WitchBossController");
            if (witch == null) return;
            var prefix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(WitchChangePhase_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var postfix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(WitchChangePhase_Post), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var mi in AccessTools.GetDeclaredMethods(witch).Where(m => m.Name == "ChangePhase" && !m.IsStatic))
            {
                try { harmony.Patch(mi, prefix: prefix, postfix: postfix); Log.Info($"[WitchPhase] patched WitchBossController.ChangePhase({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})"); }
                catch (Exception ex) { Log.Error($"[WitchPhase] patch WitchBossController.ChangePhase failed: {ex.Message}"); }
            }
        }

        /// <summary>Phase 5.4-G4: diagnostic prefix on WitchPhase2.InitPhase + prefix/postfix on ShowWitches — confirm
        /// whether the Client runs ShowWitches at all in round 1 and capture the final real dome index. Read-only.</summary>
        private static void PatchWitchPhase2Probe(Harmony harmony)
        {
            if (!Plugin.Cfg.LogWitchPhase2Probe.Value) { Log.Info("[WitchP2Probe] disabled by config."); return; }
            var p2 = FindType("WitchPhase2", "PerfectRandom.Sulfur.Gameplay.WitchPhase2");
            if (p2 == null) { Log.Info("[WitchP2Probe] WitchPhase2 type not found (skipped)"); return; }
            try
            {
                var init = AccessTools.Method(p2, "InitPhase");
                if (init != null) harmony.Patch(init, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchP2_InitPhase_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                var show = AccessTools.Method(p2, "ShowWitches");
                if (show != null) harmony.Patch(show,
                    prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchP2_ShowWitches_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchP2_ShowWitches_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[WitchP2Probe] patched WitchPhase2.InitPhase({(init != null)}) ShowWitches({(show != null)})");
            }
            catch (Exception ex) { Log.Error($"[WitchP2Probe] patch failed: {ex.Message}"); }
        }

        // LD-Sandstorm / F4: suppress the Desert boss's per-frame phase combat on the client (host-authoritative fight).
        private static void PatchDesertPhaseSuppression(Harmony harmony, Type desert)
        {
            if (desert == null) { Log.Info("[BossCombat] DesertClause type not found (phase suppression skipped)"); return; }
            try
            {
                // Approach B (host-authoritative): on the client the boss is a passive puppet. Suppress its per-frame
                // phase combat (UpdatePhases*) AND its local phase TRANSITION (TransitionTo). The latter is critical:
                // BossPhase.Update runs its own CheckValidTransition → TransitionTo → PhaseTransition() which sets the
                // boss invulnerable and is only cleared by the (suppressed) UpdatePhases → the client boss half-transitions
                // and freezes at the phase threshold (LogOutput279: BossState apply stalls at ~70%). Blocking TransitionTo
                // keeps the client boss fully passive; phase/dialog/adds come host-authoritatively (host-mirror stages).
                foreach (var name in new[] { "UpdatePhasesDeltaTime", "UpdatePhasesFixedTime", "TransitionTo" })
                {
                    var m = AccessTools.Method(desert, name);
                    if (m != null)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_UpdatePhases_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.Info($"[BossCombat] patched DesertClause.{name}({m != null}) — client combat/transition suppression");
                }

                // Position: the intro's RepositionBossFromCamera places the rig 12 m in front of the LOCAL camera, so
                // each end puts the boss somewhere different (~35 m apart). Block it on the client — the boss stays at its
                // placed (seed-synced) position and follows the host; the host keeps its own reposition for the cinematic.
                var repos = AccessTools.Method(desert, "RepositionBossFromCamera");
                if (repos != null)
                    harmony.Patch(repos, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_RepositionBossFromCamera_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[BossCombat] patched DesertClause.RepositionBossFromCamera({repos != null}) — client reposition suppression");
            }
            catch (Exception ex) { Log.Error($"[BossCombat] Desert phase suppression patch failed: {ex.Message}"); }
        }

        // Returns false (skip the phase combat) on the client once the boss has started; true otherwise (host / intro).
        private static bool Desert_UpdatePhases_Pre(object __instance)
        {
            try { return !SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.ShouldSuppressClientBossCombat(__instance); }
            catch { return true; }
        }

        // Returns false (skip the camera reposition) on a joined client so the boss stays host-positioned; true on host.
        private static bool Desert_RepositionBossFromCamera_Pre()
        {
            try { return !SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.ShouldSuppressClientBossReposition(); }
            catch { return true; }
        }

        // LD-Sandstorm / F4 Stage 3: phase-action / presentation sync. The Desert boss dismounts its pike via the private
        // OnBossJump / OnBossLand / OnBossLandTerminator callbacks (pikeCarrier.onJump/onLand delegates), which set the
        // "JumpingOffPike" animator bool. On the host these run the real dismount; on the client the pike machinery is
        // suppressed so they never fire. Postfix them (Desert-scoped) so the host broadcasts the dismount; the client
        // replays the animator state via NetBossDiscreteEvent ("BossJump"/"BossLand"). Same-named methods on a different
        // (legacy) boss type are NOT patched — only DesertClauseBossFightHelper.
        private static void PatchDesertDismount(Harmony harmony, Type desert)
        {
            if (desert == null) { Log.Info("[BossPhaseAction] DesertClause type not found (dismount sync skipped)"); return; }
            try
            {
                var jump = new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_OnBossJump_Post), BindingFlags.Static | BindingFlags.NonPublic));
                var land = new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_OnBossLand_Post), BindingFlags.Static | BindingFlags.NonPublic));
                var j = AccessTools.Method(desert, "OnBossJump");
                if (j != null) harmony.Patch(j, postfix: jump);
                Log.Info($"[BossPhaseAction] patched DesertClause.OnBossJump({j != null})");
                foreach (var name in new[] { "OnBossLand", "OnBossLandTerminator" })
                {
                    var m = AccessTools.Method(desert, name);
                    if (m != null) harmony.Patch(m, postfix: land);
                    Log.Info($"[BossPhaseAction] patched DesertClause.{name}({m != null})");
                }
                // LD-Sandstorm / F4 (sandstorm presentation sync): the arena-edge sandstorm is `Anim_OnTriggerSandstorm`
                // (→ StartSandstorm: sandstorm anim + music + fog, releases the intro Cinematic lock at its tail). Natively
                // it is an animation-event on the intro clip, so it only fires on an end whose local player reads the intro
                // to the end — the host in a client-first start (player elsewhere) and any client preempted by combat-entry
                // never play it (Log301). Postfix it (Desert-scoped): the host broadcasts the sandstorm so every end mirrors
                // it; a per-key guard makes it run exactly once per end whether via the native anim-event or a mirror invoke.
                var sand = new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_Anim_OnTriggerSandstorm_Pre), BindingFlags.Static | BindingFlags.NonPublic));
                var s = AccessTools.Method(desert, "Anim_OnTriggerSandstorm");
                if (s != null) harmony.Patch(s, prefix: sand);
                Log.Info($"[BossPhaseAction] patched DesertClause.Anim_OnTriggerSandstorm({s != null})");
            }
            catch (Exception ex) { Log.Error($"[BossPhaseAction] Desert dismount patch failed: {ex.Message}"); }
        }

        // Host-only (guarded inside): broadcast the pike jump-off so the passive client plays the "JumpingOffPike" anim.
        private static void Desert_OnBossJump_Post(object __instance, bool __runOriginal)
        {
            if (__runOriginal) NetBossEncounterManager.OnHostBossPikeDismount(__instance, jumping: true);
        }

        // Host-only (guarded inside): broadcast the pike landing so the passive client clears "JumpingOffPike".
        private static void Desert_OnBossLand_Post(object __instance, bool __runOriginal)
        {
            if (__runOriginal) NetBossEncounterManager.OnHostBossPikeDismount(__instance, jumping: false);
        }

        // Prefix on BOTH ends. Runs once per key (dedup): first call (native intro anim-event OR a mirror invoke) runs the
        // real StartSandstorm; the host also broadcasts the sandstorm so every end mirrors it. A repeat call is blocked so
        // StartSandstorm can't run twice. Returns true to run the original, false to skip. See OnLocalBossSandstorm.
        private static bool Desert_Anim_OnTriggerSandstorm_Pre(object __instance)
            => NetBossEncounterManager.OnLocalBossSandstorm(__instance);

        // LD-Sandstorm / F4: DesertPikeCarrier hooks — Update (pike-visual clone + probe) and ActivateShooting (P1
        // machine-gun target rotation over all players). Regular enemy pikes are filtered out inside the handlers.
        private static void PatchDesertPikeAttach(Harmony harmony, Type pikeCarrier)
        {
            if (pikeCarrier == null) { Log.Info("[BossPhaseAction] DesertPikeCarrier type not found (pike-riding visibility skipped)"); return; }
            try
            {
                // Diagnostic (throttled inside): dump the Desert boss body's render/transform state on both ends.
                var upd = AccessTools.Method(pikeCarrier, "Update");
                if (upd != null) harmony.Patch(upd, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_PikeUpdate_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[BossPhaseAction] patched DesertPikeCarrier.Update({upd != null}) — visibility probe");
                // P1 target rotation: ActivateShooting starts the machine-gun burst at AiAgent.target; the host rotates
                // that target over the in-room players on each burst edge so the boss attacks everyone in turn (EMP-7
                // style), instead of natively locking onto the host player forever.
                var act = AccessTools.Method(pikeCarrier, "ActivateShooting");
                if (act != null) harmony.Patch(act, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_ActivateShooting_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[BossTargetRotate] patched DesertPikeCarrier.ActivateShooting({act != null})");
                // F4-P1JMP: the boss pike's jump timing/points are natively rolled per end → the boss pops out of the sand
                // at different times/places on every end. Host broadcasts each boss-pike JumpTowards (postfix); the client
                // blocks its own local boss-pike jumps (prefix) and replays the host's exact arc via the reentry guard.
                var jmp = AccessTools.Method(pikeCarrier, "JumpTowards");
                if (jmp != null) harmony.Patch(jmp,
                    prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_JumpTowards_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_JumpTowards_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[PikeJumpSync] patched DesertPikeCarrier.JumpTowards({jmp != null})");
            }
            catch (Exception ex) { Log.Error($"[BossPhaseAction] DesertPikeCarrier patch failed: {ex.Message}"); }
        }

        private static void Desert_PikeUpdate_Post()
        {
            NetBossEncounterManager.UpdateBossPikeVisual();
            NetBossEncounterManager.ProbeDesertVisibility();
        }

        // Host-only (guarded inside): rotate the boss pike's machine-gun aim over the in-room players per burst.
        private static void Desert_ActivateShooting_Pre(object __instance)
            => NetBossEncounterManager.OnHostBossPikeActivateShooting(__instance);

        // CLIENT: block the local sim's own boss-pike jumps (host-authoritative; reentry-replayed jumps pass).
        private static bool Desert_JumpTowards_Pre(object __instance)
            => !NetBossEncounterManager.ShouldBlockClientBossPikeJump(__instance);

        // HOST: broadcast each boss-pike jump so clients replay the identical native arc.
        private static void Desert_JumpTowards_Post(object __instance, bool __runOriginal)
        {
            if (__runOriginal) NetBossEncounterManager.OnHostBossPikeJumpStarted(__instance);
        }

        // LD-Sandstorm / F4-MISSILE D1: host-authoritative missile firing windows.
        private static void PatchDesertMissile(Harmony harmony, Type missileBase)
        {
            if (missileBase == null) { Log.Info("[DesertMissile] DesertMissileBase type not found (missile sync skipped)"); return; }
            try
            {
                var start = AccessTools.Method(missileBase, "StartMissiles");
                if (start != null) harmony.Patch(start,
                    prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_MissileStart_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_MissileStart_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                var stop = AccessTools.Method(missileBase, "StopRockets");
                if (stop != null) harmony.Patch(stop, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_MissileStop_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                // D2: each real rocket fired at the local player → also spawn ghost visual rockets on the other players.
                var spawn = AccessTools.Method(missileBase, "SpawnRealRocket");
                if (spawn != null) harmony.Patch(spawn, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_MissileRocket_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[DesertMissile] patched DesertMissileBase.StartMissiles({start != null}) StopRockets({stop != null}) SpawnRealRocket({spawn != null})");
            }
            catch (Exception ex) { Log.Error($"[DesertMissile] patch failed: {ex.Message}"); }
        }

        // F4-MISSILE D2: the ghost-rocket damage gate on DesertMissileRocket itself.
        private static void PatchDesertMissileRocket(Harmony harmony, Type rocket)
        {
            if (rocket == null) { Log.Info("[DesertMissile] DesertMissileRocket type not found (ghost rockets skipped)"); return; }
            try
            {
                var dmg = AccessTools.Method(rocket, "CheckAndDamageUnit");
                if (dmg != null) harmony.Patch(dmg, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_RocketDamage_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                var boom = AccessTools.Method(rocket, "DestroyMissile");
                if (boom != null) harmony.Patch(boom, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(Desert_RocketDestroy_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[DesertMissile] patched DesertMissileRocket.CheckAndDamageUnit({dmg != null}) DestroyMissile({boom != null})");
            }
            catch (Exception ex) { Log.Error($"[DesertMissile] rocket patch failed: {ex.Message}"); }
        }

        // CLIENT: block the base's own StartMissiles (host-authoritative windows; host-replayed starts pass via reentry).
        private static bool Desert_MissileStart_Pre(object __instance)
            => !NetBossEncounterManager.ShouldBlockClientMissileStart(__instance);

        // HOST: broadcast the missile start so clients begin firing the same base at the same time.
        private static void Desert_MissileStart_Post(object __instance, bool __runOriginal)
        {
            if (__runOriginal) NetBossEncounterManager.OnHostMissileStart(__instance);
        }

        // HOST: broadcast the missile stop so clients stop the same base (fixes the client firing on in later phases).
        private static void Desert_MissileStop_Post(object __instance, bool __runOriginal)
        {
            if (__runOriginal) NetBossEncounterManager.OnHostMissileStop(__instance);
        }

        // BOTH ends: a real rocket fired at the local player → add ghost visual rockets on the other players (D2).
        private static void Desert_MissileRocket_Post(object __instance)
            => NetBossEncounterManager.OnMissileRealRocketFired(__instance);

        // D2: skip the damage pass for a ghost VISUAL rocket (explosion VFX still plays; the id gate is per-instance).
        private static bool Desert_RocketDamage_Pre(object __instance)
            => !NetBossEncounterManager.IsGhostRocket(__instance);

        // D2: the rocket exploded — drop its ghost id so the pooled instance can be reused as a real rocket.
        private static void Desert_RocketDestroy_Post(object __instance)
            => NetBossEncounterManager.OnRocketDestroyed(__instance);

        private static void WitchP2_InitPhase_Pre(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.WitchPhase2Probe.OnInitPhase(__instance);

        // Phase 5.4-G5: prefix also BLOCKS the Client's local random ShowWitches (the manifest drives the dome layout).
        // Returns false to block; true to run (Host / single-player / reentry). The probe log stays.
        private static bool WitchP2_ShowWitches_Pre(object __instance)
        {
            SULFURTogether.Networking.Gameplay.Boss.WitchPhase2Probe.OnShowWitchesEnter(__instance);
            try { return SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnLocalWitchShowWitches_Pre(__instance); }
            catch { return true; }
        }

        // Phase 5.4-G5: postfix (Host ran) captures + broadcasts the final dome layout manifest.
        private static void WitchP2_ShowWitches_Post(object __instance, bool __runOriginal)
        {
            SULFURTogether.Networking.Gameplay.Boss.WitchPhase2Probe.OnShowWitchesExit(__instance);
            if (__runOriginal)
                SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostWitchShowWitches(__instance);
        }

        /// <summary>Phase 5.4-G5: postfix WitchPhase2.RealWitchTakeDamage / IllusionTakeDamage so the Host broadcasts the
        /// hide result (the Client routes Phase 2 hits to the Host, so its own handlers never fire).</summary>
        private static void PatchWitchP2HitResults(Harmony harmony)
        {
            var p2 = FindType("WitchPhase2", "PerfectRandom.Sulfur.Gameplay.WitchPhase2");
            if (p2 == null) { Log.Info("[WitchP2] WitchPhase2 type not found (result hooks skipped)"); return; }
            try
            {
                var real = AccessTools.Method(p2, "RealWitchTakeDamage");
                if (real != null) harmony.Patch(real, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchP2_RealHit_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                var ill = AccessTools.Method(p2, "IllusionTakeDamage");
                if (ill != null) harmony.Patch(ill, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchP2_IllusionHit_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[WitchP2] patched RealWitchTakeDamage({real != null}) IllusionTakeDamage({ill != null})");
            }
            catch (Exception ex) { Log.Error($"[WitchP2] patch hit results failed: {ex.Message}"); }
        }

        private static void WitchP2_RealHit_Post(object __instance, object unit, bool __runOriginal)
        {
            if (!__runOriginal) return;
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostWitchP2Hit(__instance, unit, realHit: true);
        }

        private static void WitchP2_IllusionHit_Post(object __instance, object unit, bool __runOriginal)
        {
            if (!__runOriginal) return;
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostWitchP2Hit(__instance, unit, realHit: false);
        }

        /// <summary>Phase 5.4-G7b: prefix + postfix on WitchBossController.WitchDeath. The real WitchDeath crashes on the
        /// Client at EquipmentManager.AmuletHoldable → GetHoldableInSlot → equippedItems[Amulet] (KeyNotFoundException
        /// "Amulet"; the Client never equipped the amulet), aborting the rest. The PREFIX blocks the original on a joined
        /// Client and runs a safe replica (everything except the amulet block + host-only PlayerProgress); the POSTFIX
        /// marks the encounter terminal on the Host.</summary>
        private static void PatchWitchDeath(Harmony harmony)
        {
            var witch = FindType("WitchBossController", "PerfectRandom.Sulfur.Gameplay.WitchBossController");
            if (witch == null) { Log.Info("[WitchDeath] WitchBossController type not found"); return; }
            var wd = AccessTools.Method(witch, "WitchDeath");
            if (wd == null) { Log.Info("[WitchDeath] WitchBossController.WitchDeath not found"); return; }
            try
            {
                harmony.Patch(wd,
                    prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchDeath_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(WitchDeath_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info("[WitchDeath] patched WitchBossController.WitchDeath (prefix+postfix)");
            }
            catch (Exception ex) { Log.Error($"[WitchDeath] patch WitchDeath failed: {ex.Message}"); }
        }

        // Returns false to BLOCK the crashing original on a joined Client (the manager runs a safe replica instead).
        private static bool WitchDeath_Pre(object __instance)
        {
            try { return SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnLocalWitchDeath_Pre(__instance); }
            catch { return true; }
        }

        private static void WitchDeath_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostWitchDeath(__instance);
        }

        // Returns false to BLOCK a joined Client's self-initiated phase transition (Host-authoritative phase).
        private static bool WitchChangePhase_Pre(object __instance, object phase)
        {
            try { return SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnLocalWitchChangePhase_Pre(__instance, Convert.ToInt32(phase)); }
            catch { return true; }
        }

        private static void WitchChangePhase_Post(object __instance, object phase, bool __runOriginal)
        {
            if (!__runOriginal) return;
            int phaseInt; try { phaseInt = Convert.ToInt32(phase); } catch { phaseInt = -1; }
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostWitchChangePhase_Post(__instance, phaseInt);
            // Keep the immediate health BossState broadcast on phase change (periodic stream also covers health).
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostBossPhaseChanged(__instance);
        }

        /// <summary>Phase 5.4-F4: postfix the fixed-point discrete mechanic methods so the Host broadcasts them and
        /// the Client mirrors the same pool/dig.</summary>
        private static void PatchDiscrete(Harmony harmony, Type? type, params string[] methodNames)
        {
            if (type == null) return;
            var prefix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(BossDiscrete_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var postfix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(BossDiscrete_Post), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var name in methodNames)
            {
                var methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name && !m.IsStatic).ToList();
                if (methods.Count == 0) { Log.Info($"[CousinPool] {type.Name}.{name} not found (skipped)"); continue; }
                foreach (var mi in methods)
                {
                    try { harmony.Patch(mi, prefix: prefix, postfix: postfix); Log.Info($"[CousinPool] patched {type.Name}.{name}"); }
                    catch (Exception ex) { Log.Error($"[CousinPool] patch failed {type.Name}.{name}: {ex.Message}"); }
                }
            }
        }

        // Returns false to BLOCK the client's own pool decision (host-authoritative).
        private static bool BossDiscrete_Pre(object __instance, MethodBase __originalMethod)
        {
            try { return SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnLocalBossDiscreteEvent_Pre(__instance, __originalMethod?.Name ?? "?"); }
            catch { return true; }
        }

        private static void BossDiscrete_Post(object __instance, MethodBase __originalMethod, bool __runOriginal)
        {
            if (!__runOriginal) return;
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnLocalBossDiscreteEvent(__instance, __originalMethod?.Name ?? "?");
        }

        /// <summary>Phase 5.4-F4: postfix-only on the boss's terminal death method so the Host broadcasts an encounter
        /// death. No blocking prefix — the Client's own death (via owner.Die()) must run its real cleanup.</summary>
        private static void PatchDeath(Harmony harmony, Type? type, params string[] methodNames)
        {
            if (type == null) return;
            var postfix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(BossDeath_Post), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var name in methodNames)
            {
                foreach (var mi in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name && !m.IsStatic))
                {
                    try { harmony.Patch(mi, postfix: postfix); Log.Info($"[CousinDeath] patched {type.Name}.{name}"); }
                    catch (Exception ex) { Log.Error($"[CousinDeath] patch failed {type.Name}.{name}: {ex.Message}"); }
                }
            }
        }

        private static void BossDeath_Post(object __instance, MethodBase __originalMethod, bool __runOriginal)
        {
            if (!__runOriginal) return;
            SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnHostBossDeath(__instance, __originalMethod?.Name ?? "?");
        }

        /// <summary>Phase PF-ArmDefer (issue 1): prefix CousinHelper.SpawnArm so the manager can BLOCK the behavior-tree
        /// intro arm (deferred to the dialog-close fight commit) while letting the mid-fight Reappear arm + the commit
        /// replay through. Patches every SpawnArm overload (there is one: SpawnArm(Vector3, bool)).</summary>
        private static void PatchIntroArm(Harmony harmony, Type? type)
        {
            if (type == null) { Log.Info("[BossArmDefer] CousinHelper type not found (skipped)"); return; }
            var prefix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(CousinSpawnArm_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == "SpawnArm" && !m.IsStatic).ToList();
            if (methods.Count == 0) { Log.Info($"[BossArmDefer] {type.Name}.SpawnArm not found (skipped)"); return; }
            foreach (var mi in methods)
            {
                try { harmony.Patch(mi, prefix: prefix); Log.Info($"[BossArmDefer] patched {type.Name}.SpawnArm({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})"); }
                catch (Exception ex) { Log.Error($"[BossArmDefer] patch failed {type.Name}.SpawnArm: {ex.Message}"); }
            }
        }

        // Returns false to BLOCK the deferred intro arm (replayed at the dialog-close fight commit); true otherwise.
        private static bool CousinSpawnArm_Pre(object __instance)
        {
            try { return SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.OnLocalIntroArmSpawn(__instance); }
            catch { return true; }
        }

        /// <summary>Phase 5.4-F5: prefix + postfix LuciaBossFightHelper.EyeDied(Unit). Confirmed by decompilation:
        /// each eye's Unit gets <c>onDeath += EyeDied</c>; EyeDied removes from spawnedEyes and, on the last, calls
        /// RestartPhases. The prefix blocks the joined Client's local EyeDied (so it can't unlock the body itself);
        /// the postfix lets the Host broadcast the authoritative remaining count.</summary>
        private static void PatchLuciaEye(Harmony harmony, Type? type)
        {
            if (type == null) { Log.Info("[LuciaEye] LuciaBossFightHelper type not found (skipped)"); return; }
            var prefix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(LuciaEyeDied_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var postfix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(LuciaEyeDied_Post), BindingFlags.Static | BindingFlags.NonPublic));
            var methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == "EyeDied" && !m.IsStatic).ToList();
            if (methods.Count == 0) { Log.Info("[LuciaEye] LuciaBossFightHelper.EyeDied not found (skipped)"); return; }
            foreach (var mi in methods)
            {
                try { harmony.Patch(mi, prefix: prefix, postfix: postfix); Log.Info($"[LuciaEye] patched {type.Name}.EyeDied({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})"); }
                catch (Exception ex) { Log.Error($"[LuciaEye] patch failed {type.Name}.EyeDied: {ex.Message}"); }
            }
        }

        // Returns false to BLOCK the joined Client's own EyeDied (it reports the kill to the Host instead).
        private static bool LuciaEyeDied_Pre(object __instance, object eyeUnit)
        {
            try { return NetBossEncounterManager.OnLocalLuciaEyeDied(__instance, eyeUnit); }
            catch { return true; }
        }

        private static void LuciaEyeDied_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            NetBossEncounterManager.OnHostLuciaEyeDied(__instance);
        }

        /// <summary>Phase 5.4-F6: prefix + postfix LuciaBossFightHelper.OnBossDead(Unit) (the overridden death handler
        /// bound to bossUnit.onDeath in TriggerFight). Prefix isolates the host-only loot/save body on the client;
        /// postfix lets the Host broadcast the terminal death.</summary>
        private static void PatchLuciaDeath(Harmony harmony, Type? type)
        {
            if (type == null) { Log.Info("[LuciaDeath] LuciaBossFightHelper type not found (skipped)"); return; }
            var prefix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(LuciaOnBossDead_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var postfix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(LuciaOnBossDead_Post), BindingFlags.Static | BindingFlags.NonPublic));
            var methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == "OnBossDead" && !m.IsStatic).ToList();
            if (methods.Count == 0) { Log.Info("[LuciaDeath] LuciaBossFightHelper.OnBossDead not declared on type (skipped)"); return; }
            foreach (var mi in methods)
            {
                try { harmony.Patch(mi, prefix: prefix, postfix: postfix); Log.Info($"[LuciaDeath] patched {type.Name}.OnBossDead({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})"); }
                catch (Exception ex) { Log.Error($"[LuciaDeath] patch failed {type.Name}.OnBossDead: {ex.Message}"); }
            }
        }

        // Returns false to BLOCK the loot/save body on a joined client (presentation is replayed by the manager).
        private static bool LuciaOnBossDead_Pre(object __instance, object unit)
        {
            try { return NetBossEncounterManager.OnLocalLuciaBossDead_Pre(__instance, unit); }
            catch { return true; }
        }

        private static void LuciaOnBossDead_Post(object __instance, object unit, bool __runOriginal)
        {
            if (!__runOriginal) return;
            NetBossEncounterManager.OnHostLuciaBossDead_Post(__instance, unit);
        }

        /// <summary>Phase 5.4-E2: postfix-probe the real boss lifecycle methods so we can confirm the host-authorized
        /// start chain reaches a consistent state (fightStarted / phase / death) on both ends. Read-only.</summary>
        private static void ApplyLifecycleProbe(Harmony harmony)
        {
            if (!Plugin.Cfg.EnableBossLifecycleProbe.Value) { Log.Info("[BossLifecycle] disabled by config."); return; }

            // Generic BossFightHelper family (covers DesertClause via base.* calls). Damage hook is throttled.
            var bfh = FindType("BossFightHelper", "PerfectRandom.Sulfur.Core.BossFightHelper", "PerfectRandom.Sulfur.Gameplay.BossFightHelper");
            PatchProbe(harmony, bfh, throttle: false, "TriggerFight", "TransitionTo", "StartPhase", "OnBossDead");
            PatchProbe(harmony, bfh, throttle: true, "OnDamageReceieved");

            // BossPhase transition internals (no adapter — probe reads generic fields).
            var phase = FindType("BossPhase", "PerfectRandom.Sulfur.Gameplay.BossPhase");
            PatchProbe(harmony, phase, throttle: false, "StartBossPhases", "StartNextPhase", "StartTransition", "SetTransitionVars");

            // Witch standalone.
            var witch = FindType("WitchBossController", "PerfectRandom.Sulfur.Gameplay.WitchBossController");
            PatchProbe(harmony, witch, throttle: false, "EventStarted", "StartFight", "ChangePhase", "WitchDeath", "TeleportPlayerTo");
            PatchProbe(harmony, witch, throttle: true, "OnDamageMainWitch");

            // Cousin standalone.
            var cousin = FindType("CousinHelper", "PerfectRandom.Sulfur.Gameplay.CousinHelper");
            PatchProbe(harmony, cousin, throttle: false, "Trigger", "TriggerIntro", "Introduction", "StartFight", "Submerge", "Reappear", "MoveToNewPool", "CousinDeath");

            // Phase PF-0: Witch arena room-seal timing (the blockade SetActive calls are animation-event driven, so the
            // client — which suppresses the boss intro animation chain — may leave the seal in the wrong state). Probe
            // the seal/unseal entrypoints to compare host vs client timing before building the PF-2 seal mirror.
            var witchAnim = FindType("WitchAnimationControl", "PerfectRandom.Sulfur.Gameplay.WitchAnimationControl");
            PatchProbe(harmony, witchAnim, throttle: false, "EnableChurchBlockade", "LookAtWitch", "EnableOutsideTrigger");

            Log.Info("[BossLifecycle] lifecycle probe hooks registered.");
        }

        private static void PatchProbe(Harmony harmony, Type? type, bool throttle, params string[] methodNames)
        {
            if (type == null) return;
            var postfix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(throttle ? nameof(BossLifecycle_Post_Throttled) : nameof(BossLifecycle_Post),
                    BindingFlags.Static | BindingFlags.NonPublic));

            foreach (var name in methodNames)
            {
                var methods = AccessTools.GetDeclaredMethods(type)
                    .Where(m => m.Name == name && !m.IsStatic && !m.IsAbstract).ToList();
                if (methods.Count == 0) { Log.Info($"[BossLifecycle] {type.Name}.{name} not found (skipped)"); continue; }
                foreach (var mi in methods)
                {
                    try { harmony.Patch(mi, postfix: postfix); }
                    catch (Exception ex) { Log.Error($"[BossLifecycle] patch failed {type.Name}.{name}: {ex.Message}"); }
                }
            }
        }

        private static void BossLifecycle_Post(object __instance, MethodBase __originalMethod, bool __runOriginal)
            => SULFURTogether.Networking.Gameplay.Boss.BossLifecycleProbe.OnLifecycle(__instance, __originalMethod?.Name ?? "?", throttle: false, ran: __runOriginal);

        private static void BossLifecycle_Post_Throttled(object __instance, MethodBase __originalMethod, bool __runOriginal)
            => SULFURTogether.Networking.Gameplay.Boss.BossLifecycleProbe.OnLifecycle(__instance, __originalMethod?.Name ?? "?", throttle: true, ran: __runOriginal);

        /// <summary>Phase PF-0: postfix-only probes on the room-seal primitives (read-only). Gated by LogBossPreFight.</summary>
        private static void PatchArenaDoorProbe(Harmony harmony)
        {
            if (!Plugin.Cfg.LogBossPreFight.Value) { Log.Info("[ArenaDoor] probe disabled by config (LogBossPreFight=false)."); return; }
            try
            {
                var door = FindType("DoorBlocker", "PerfectRandom.Sulfur.Core.DoorBlocker");
                if (door != null)
                {
                    var close = AccessTools.Method(door, "CloseDoor");
                    if (close != null) harmony.Patch(close, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(DoorClose_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.Info($"[ArenaDoor] patched DoorBlocker.CloseDoor({close != null})");
                }
                var allDead = FindType("AllDeadTrigger", "PerfectRandom.Sulfur.Core.AllDeadTrigger");
                if (allDead != null)
                {
                    foreach (var name in new[] { "RegisterDeath", "CheckAllDead" })
                    {
                        var mi = AccessTools.Method(allDead, name);
                        if (mi != null) harmony.Patch(mi, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(AllDead_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                    Log.Info("[ArenaDoor] patched AllDeadTrigger.RegisterDeath/CheckAllDead");
                }
            }
            catch (Exception ex) { Log.Error($"[ArenaDoor] probe patch failed: {ex.Message}"); }
        }

        /// <summary>Phase PF: postfix-only probes on the dialog-open chokepoints (read-only). Gated by LogBossPreFight.</summary>
        private static void PatchDialogFlowProbe(Harmony harmony)
        {
            // Needed when EITHER the read-only dialog-flow probe is on OR the Plan B fight gate is on (the gate reads
            // boss-dialog open via Npc.Interact and dialog close via DialogController.SetCurrentSpeakable).
            if (!Plugin.Cfg.LogBossPreFight.Value && !Plugin.Cfg.GateBossFightOnDialogClose.Value)
            { Log.Info("[DialogFlow] hooks disabled (LogBossPreFight=false, GateBossFightOnDialogClose=false)."); return; }
            try
            {
                var npc = FindType("Npc", "PerfectRandom.Sulfur.Core.Units.Npc");
                if (npc != null)
                {
                    var interact = AccessTools.GetDeclaredMethods(npc)
                        .FirstOrDefault(m => m.Name == "Interact" && !m.IsStatic && m.GetParameters().Length == 1);
                    if (interact != null) harmony.Patch(interact,
                        prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(NpcInteract_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(NpcInteract_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.Info($"[DialogFlow] patched Npc.Interact(Player)({interact != null})");
                }
                var dc = FindType("DialogController", "PerfectRandom.Sulfur.Core.DialogController");
                if (dc != null)
                {
                    var scs = AccessTools.Method(dc, "SetCurrentSpeakable");
                    if (scs != null) harmony.Patch(scs, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(SetCurrentSpeakable_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.Info($"[DialogFlow] patched DialogController.SetCurrentSpeakable({scs != null})");
                    // F4-P2DLG dialog input probe (read-only): why the host can't advance the mid-fight dialog by clicking.
                    var accept = AccessTools.Method(dc, "AcceptDialogOption");
                    if (accept != null) harmony.Patch(accept, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(AcceptDialogOption_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    var mcr = AccessTools.Method(dc, "OnMultipleChoiceRequest");
                    if (mcr != null) harmony.Patch(mcr, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(MultipleChoiceRequest_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    var dcUpd = AccessTools.Method(dc, "Update");
                    if (dcUpd != null) harmony.Patch(dcUpd, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(DialogControllerUpdate_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    var dStart = AccessTools.Method(dc, "OnDialogueStarted");
                    if (dStart != null) harmony.Patch(dStart, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(OnDialogueStarted_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    var dFin = AccessTools.Method(dc, "OnDialogueFinished");
                    if (dFin != null) harmony.Patch(dFin, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(OnDialogueFinished_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.Info($"[DialogInput] patched DialogController.AcceptDialogOption({accept != null}) OnMultipleChoiceRequest({mcr != null}) Update({dcUpd != null}) started({dStart != null}) finished({dFin != null})");
                }
                var pt = FindType("PlayerTrigger", "PerfectRandom.Sulfur.Core.World.PlayerTrigger");
                if (pt != null)
                {
                    var trig = AccessTools.Method(pt, "Trigger", new[] { typeof(UnityEngine.GameObject) });
                    if (trig != null) harmony.Patch(trig, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(PlayerTrigger_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.Info($"[DialogFlow] patched PlayerTrigger.Trigger({trig != null})");
                }
            }
            catch (Exception ex) { Log.Error($"[DialogFlow] probe patch failed: {ex.Message}"); }
        }

        // RM-2b: block the boss dialog for an OUT-OF-ROOM player (the boss appears but they get no dialog cutscene).
        private static bool NpcInteract_Pre(object __instance)
        {
            // EMP-6g: a late arrival to the Emperor phase-2 pit hits the (now-disabled) spider dialog → pull them to the
            // fight-starter instead of a dead interaction. Handled + swallowed here when it applies.
            try { if (SULFURTogether.Networking.Gameplay.Boss.NetEmperorSpiderSync.TryTeleportLateP2Player(__instance)) return false; } catch { }
            try { if (NetBossEncounterManager.ShouldBlockBossDialogNpc(__instance)) return false; } catch { }
            return true;
        }

        private static void NpcInteract_Post(object __instance, bool __runOriginal)
        {
            SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnNpcInteract(__instance, __runOriginal);
            // Phase PF (Plan B): a boss Npc opened its dialog → arm the dialog-close fight commit for its encounter.
            if (__runOriginal) NetBossEncounterManager.NotifyBossDialogOpened(__instance);
            // LD-Sandstorm / F4 Stage 2: host broadcasts a mid-fight boss dialog (Desert) so the passive client plays it.
            if (__runOriginal) NetBossEncounterManager.OnHostBossDialogInteract(__instance);
        }

        // ================================================================== RM-2b: out-of-room intro effect suppression

        /// <summary>Patch the boss-intro player-facing effects so they no-op while a local OUT-OF-ROOM player is in a boss
        /// Introduction: the camera turn + the Cinematic controller/invulnerability lock. The boss still appears; the
        /// out-of-room player is not dragged into the cutscene. Plus a prefix/postfix on CousinHelper.Introduction that
        /// raises the suppression window around the native (or forced) intro. Gated by GateBossDialogToInRoom.</summary>
        private static void ApplyCutsceneSuppressionPatches(Harmony harmony)
        {
            if (!Plugin.Cfg.GateBossDialogToInRoom.Value) { Log.Info("[BossDialogCutscene] suppression patches off (GateBossDialogToInRoom=false)"); return; }
            try
            {
                // Raise/lower the suppression window around CousinHelper.Introduction (covers the native behavior-tree intro
                // when the boss is woken near a remote player; the forced-appearance path sets the flag itself).
                var cousin = FindType("CousinHelper", "PerfectRandom.Sulfur.Gameplay.CousinHelper", "PerfectRandom.Sulfur.Core.CousinHelper");
                var intro = cousin == null ? null : AccessTools.Method(cousin, "Introduction", Type.EmptyTypes);
                if (intro != null)
                    harmony.Patch(intro,
                        prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(BossIntroSuppress_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(BossIntroSuppress_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[BossDialogCutscene] patched CousinHelper.Introduction suppression ({intro != null})");

                // Invariant: keep the boss invulnerable until the fight commits — DoneAppearing (rise-anim end) clears the
                // boss's invuln; re-assert it pre-commit.
                var done = cousin == null ? null : AccessTools.Method(cousin, "DoneAppearing", Type.EmptyTypes);
                if (done != null) harmony.Patch(done, postfix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(BossDoneAppearing_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[BossDialogCutscene] patched CousinHelper.DoneAppearing invuln-hold ({done != null})");

                var gm = FindType("GameManager", "PerfectRandom.Sulfur.Core.GameManager");
                if (gm != null)
                {
                    foreach (var name in new[] { "ModifyControllerLock", "ModifyPlayerInvulnerability" })
                    {
                        var mi = AccessTools.GetDeclaredMethods(gm).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 2);
                        if (mi != null) harmony.Patch(mi, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(CinematicLock_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                        Log.Info($"[BossDialogCutscene] patched GameManager.{name}({mi != null})");
                    }
                }

                // RotateCameraTowardsPosition is declared on Player (GameManager.PlayerScript returns a Player), NOT a
                // "PlayerScript" type. Patch every overload.
                var ps = FindType("Player", "PerfectRandom.Sulfur.Core.Units.Player", "PerfectRandom.Sulfur.Core.Player");
                var rots = ps == null ? System.Array.Empty<MethodInfo>() : AccessTools.GetDeclaredMethods(ps).Where(m => m.Name == "RotateCameraTowardsPosition").ToArray();
                foreach (var rot in rots)
                    harmony.Patch(rot, prefix: new HarmonyMethod(typeof(BossEncounterPatches).GetMethod(nameof(RotateCamera_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info($"[BossDialogCutscene] patched Player.RotateCameraTowardsPosition x{rots.Length}");
            }
            catch (Exception ex) { Log.Error($"[BossDialogCutscene] suppression patch failed: {ex.Message}"); }
        }

        private static void BossIntroSuppress_Pre(object __instance)
        {
            try
            {
                if (!NetBossEncounterManager.IsLocalOutOfRoomForBoss(__instance)) return;
                NetBossEncounterManager.SetSuppressBossCutscene(true);
                NetBossEncounterManager.MarkIntroRanOutOfRoom(__instance); // so a later catch-up opens the dialog directly
            }
            catch { }
        }
        private static void BossIntroSuppress_Post()
        {
            try { NetBossEncounterManager.SetSuppressBossCutscene(false); } catch { }
        }

        // Skip the Cinematic controller/invulnerability lock while suppressing an out-of-room boss cutscene (only the
        // "lock on" with the Cinematic padlock — leave unlocks and other padlocks alone).
        private static bool CinematicLock_Pre(object[] __args)
        {
            try
            {
                if (!NetBossEncounterManager.IsSuppressingBossCutscene) return true;
                if (__args == null || __args.Length < 2) return true;
                bool on = __args[1] is bool b && b;
                bool cinematic = __args[0] != null && __args[0].ToString() == "Cinematic";
                if (on && cinematic) return false; // skip the lock
            }
            catch { }
            return true;
        }

        private static bool RotateCamera_Pre()
        {
            try { if (NetBossEncounterManager.IsSuppressingBossCutscene) return false; } catch { }
            return true;
        }

        private static void BossDoneAppearing_Post(object __instance)
        {
            try { NetBossEncounterManager.OnBossDoneAppearing(__instance); } catch { }
        }

        private static void AcceptDialogOption_Post(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnAcceptDialogOption(__instance);
        private static void MultipleChoiceRequest_Post(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnMultipleChoiceRequest(__instance);
        private static void DialogControllerUpdate_Post(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnDialogUpdate(__instance);
        private static void OnDialogueStarted_Post(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnDialogueStarted(__instance);
        private static void OnDialogueFinished_Post(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnDialogueFinished(__instance);

        private static void SetCurrentSpeakable_Post(object speakable)
        {
            SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnSetCurrentSpeakable(speakable);
            // Phase PF (Plan B): the dialog closed (null speakable) → if a gated boss dialog was open, commit the fight.
            if (speakable == null) NetBossEncounterManager.NotifyDialogClosed();
            // LD-Sandstorm / F4 Stage 2: dialog close sync — if a boss dialog was open, tell the client to finalize its copy.
            if (speakable == null) NetBossEncounterManager.OnHostBossDialogClosed();
            // LD-Sandstorm / F4 (intro-finish sync): track the local boss INTRO dialog open/close on EITHER end so whoever
            // reads it to the end commits the fight authoritatively (client → host request; host → force TriggerFight).
            NetBossEncounterManager.OnLocalDialogSpeakableChanged(speakable);
        }

        private static void PlayerTrigger_Post(object __instance)
            => SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnPlayerTrigger(__instance);

        private static void DoorClose_Post(object __instance, bool __runOriginal)
        {
            if (__runOriginal) SULFURTogether.Networking.Gameplay.Boss.ArenaDoorProbe.OnDoorClose(__instance);
        }

        private static void AllDead_Post(object __instance, MethodBase __originalMethod, bool __runOriginal)
        {
            if (__runOriginal) SULFURTogether.Networking.Gameplay.Boss.ArenaDoorProbe.OnAllDead(__instance, __originalMethod?.Name ?? "?");
        }

        private static Type? FindType(string shortName, params string[] fullNames)
        {
            foreach (var fn in fullNames)
            {
                var t = AccessTools.TypeByName(fn);
                if (t != null) return t;
            }
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    foreach (var t in types) if (t != null && t.Name == shortName) return t;
                }
            }
            catch { }
            Log.Info($"[BossEncounter] type not found: {shortName}");
            return null;
        }

        private static void PatchStart(Harmony harmony, Type? type, params string[] methodNames)
        {
            if (type == null) return;
            var prefix = new HarmonyMethod(typeof(BossEncounterPatches)
                .GetMethod(nameof(BossStartEntrypoint_Pre), BindingFlags.Static | BindingFlags.NonPublic));

            foreach (var name in methodNames)
            {
                // Patch every parameter overload of the named method (instance methods only).
                var methods = AccessTools.GetDeclaredMethods(type)
                    .Where(m => m.Name == name && !m.IsStatic && !m.IsAbstract).ToList();
                if (methods.Count == 0)
                {
                    Log.Info($"[BossEncounter] {type.Name}.{name} not found (skipped)");
                    continue;
                }
                foreach (var mi in methods)
                {
                    try
                    {
                        harmony.Patch(mi, prefix: prefix);
                        Log.Info($"[BossEncounter] patched {type.Name}.{name}({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BossEncounter] patch failed {type.Name}.{name}: {ex.Message}");
                    }
                }
            }
        }

        // Returns false to BLOCK the original (joined client defers to host authority); true to run normally.
        private static bool BossStartEntrypoint_Pre(object __instance, MethodBase __originalMethod)
        {
            try
            {
                string source = $"{__instance?.GetType().Name}.{__originalMethod?.Name}";
                return NetBossEncounterManager.OnLocalStartEntrypoint(__instance!, source);
            }
            catch (Exception ex)
            {
                Log.Warn($"[BossEncounter] start entrypoint prefix failed: {ex.Message}");
                return true;
            }
        }
    }
}
