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
            try { if (NetBossEncounterManager.ShouldBlockBossDialogNpc(__instance)) return false; } catch { }
            return true;
        }

        private static void NpcInteract_Post(object __instance, bool __runOriginal)
        {
            SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnNpcInteract(__instance, __runOriginal);
            // Phase PF (Plan B): a boss Npc opened its dialog → arm the dialog-close fight commit for its encounter.
            if (__runOriginal) NetBossEncounterManager.NotifyBossDialogOpened(__instance);
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

        private static void SetCurrentSpeakable_Post(object speakable)
        {
            SULFURTogether.Networking.Gameplay.Boss.BossDialogFlowProbe.OnSetCurrentSpeakable(speakable);
            // Phase PF (Plan B): the dialog closed (null speakable) → if a gated boss dialog was open, commit the fight.
            if (speakable == null) NetBossEncounterManager.NotifyDialogClosed();
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
