using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase EM-1 — Endless Mode client slave (host-authoritative world layer). See Docs/EndlessModeSyncPlan.md §4.
    ///
    /// <para>In vanilla, every peer's <c>EndlessModeManager</c> independently drives its own arena, waves, and enemy
    /// spawns. EM-0 (Log439) confirmed both ends load the <b>same arena from the same seed</b>, so a linked client keeps
    /// the arena it already built — but it must stop driving its own <b>waves</b> and <b>enemy spawns</b>, or it fights a
    /// second, unsynchronized Endless run overlaid on the host's. This makes the client a slave:</para>
    /// <list type="bullet">
    /// <item><b>StartEnemySpawning</b> (the per-wave burst coroutine) is skipped — no local enemies, no spurious
    /// Stage/Wave alerts, no local burst-index churn. The host's wave enemies arrive instead through the runtime-spawn
    /// mirror (EM-2: <c>EndlessModeManager</c> is now classified in <see cref="RuntimeSpawnManager.ClassifyOwner"/>), bound
    /// as host-authoritative puppets like DevTools/TriggerSpawn/boss-add spawns.</item>
    /// <item><b>Update</b> (the wave state machine + local XP/card triggers) is skipped — the client's <c>spawnedUnits</c>
    /// list stays empty (mirrored puppets are owned by GameManager, not the manager), which would otherwise read as
    /// 100% killed and drive divergent wave/stage/arena transitions. Host-driven wave/XP state is (re)introduced in EM-3.</item>
    /// </list>
    ///
    /// <para>The host runs everything normally; all suppression is gated on being a <b>linked client</b>
    /// (<c>BossMode == Client &amp;&amp; NetLinkState.ClientLinked</c>) so single-player and the host are untouched. No
    /// arena-selection suppression is needed (seed parity already makes the client pick the same arena — EM-0).</para>
    /// </summary>
    internal static class EndlessSyncPatches
    {
        public static int ClientWaveDriverSkipped;
        public static int ClientSpawnCoroutineSkipped;

        // EM-7: Endless card-reward world-spawn routing (Shared mode). World rewards are host-authoritative and mirrored;
        // the client suppresses its own duplicate. Loot-table Pickups ride the WID pipeline; companions ride RuntimeSpawn.
        private static FieldInfo? _crRewardType;        // CardReward.rewardType : CardRewardType
        private static FieldInfo? _crContainerPrefab;   // CardReward.containerPrefab : Container (null = plain SpawnPickup path)
        private static int _lootTableRewardValue = int.MinValue; // CardRewardType.SpawnFromLootTable
        private static int _spawnRandomAlliesRewardValue = int.MinValue; // CardRewardType.SpawnRandomAllies (companion)
        private static int _spawnNpcRewardValue          = int.MinValue; // CardRewardType.SpawnNPC (shop)
        private static int _spawnInteractableRewardValue = int.MinValue; // CardRewardType.SpawnInteractable (chest / storage / service station — EM-7e)

        // EM-7c: >0 while the HOST is inside FloatingCardManager.SpawnCompanion. `RuntimeSpawnManager.ClassifyOwner` uses
        // it to mirror only companion spawns from FloatingCardManager (shop NPCs share the owner but are EM-7d).
        internal static int HostCompanionSpawnDepth;

        // EM-7d: >0 while the HOST is inside the ExecuteReward SpawnNPC (shop) branch — same idea as the companion bracket,
        // so ClassifyOwner mirrors shop-NPC spawns as "EndlessShop" (distinct from companion). SpawnNPC has no dedicated
        // method, so the bracket is set/cleared around ExecuteReward (its first await is the shop's SpawnUnitAsync, which
        // NotePendingSpawn observes synchronously). Shop cards spawn one NPC (spawnCount 1); a multi-NPC card would only
        // mirror its first (accepted).
        internal static int HostShopSpawnDepth;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }

        /// <summary>True on a client that is linked into a host session — the host owns the Endless world.</summary>
        private static bool IsLinkedClient =>
            NetGameplaySyncBridge.BossMode == NetMode.Client && NetLinkState.ClientLinked;

        public static void Apply(Harmony harmony)
        {
            if (!Enabled) { Plugin.Log.Info("[Endless] EM-1 client slave disabled by config."); return; }
            try
            {
                var emType = AccessTools.TypeByName("EndlessModeManager")
                          ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.EndlessModeManager");
                if (emType == null) { Plugin.Log.Info("[Endless] EndlessModeManager type not found — EM-1 client slave disabled."); return; }

                int patched = 0;

                var update = AccessTools.DeclaredMethod(emType, "Update");
                if (update != null)
                {
                    harmony.Patch(update, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(Update_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                }
                else Plugin.Log.Info("[Endless] EndlessModeManager.Update not found — wave-driver suppression disabled.");

                var startSpawning = AccessTools.DeclaredMethod(emType, "StartEnemySpawning");
                if (startSpawning != null)
                {
                    harmony.Patch(startSpawning, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(StartEnemySpawning_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                }
                else Plugin.Log.Info("[Endless] EndlessModeManager.StartEnemySpawning not found — spawn suppression disabled.");

                // EM-5b: host-authoritative shared XP pickups (suppress vanilla host orbs, mint one broadcast pickup) +
                // non-freezing Independent-mode card select.
                var onEnemyDied = AccessTools.DeclaredMethod(emType, "OnEnemyDied");
                if (onEnemyDied != null)
                    harmony.Patch(onEnemyDied,
                        prefix:  new HarmonyMethod(typeof(EndlessSyncPatches).GetMethod(nameof(OnEnemyDied_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(EndlessSyncPatches).GetMethod(nameof(OnEnemyDied_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.OnEnemyDied not found — EM-5b XP pickup disabled.");

                var orbType = AccessTools.TypeByName("XPOrbManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.XPOrbManager");
                var spawnOrb = orbType != null ? AccessTools.Method(orbType, "SpawnOrb", new[] { typeof(UnityEngine.Vector3), typeof(int) }) : null;
                if (spawnOrb != null)
                    harmony.Patch(spawnOrb, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(SpawnOrb_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] XPOrbManager.SpawnOrb not found — vanilla orb suppression disabled.");

                var startTransition = AccessTools.DeclaredMethod(emType, "StartTransition");
                if (startTransition != null)
                    harmony.Patch(startTransition, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(StartTransition_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.StartTransition not found — EM-5 no-freeze card select disabled.");

                var cardSpin = AccessTools.DeclaredMethod(emType, "CardSpinComplete");
                if (cardSpin != null)
                    harmony.Patch(cardSpin, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(CardSpinComplete_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.CardSpinComplete not found — EM-5 invuln clear disabled.");

                // bug1: defer the XP-threshold card selection while the local player is downed (absorbing XP while downed
                // must not open the card panel — it soft-locks controls through/after revive). Held XP is not reset, so it
                // fires normally once revived.
                var checkXp = AccessTools.DeclaredMethod(emType, "CheckXPThreshold");
                if (checkXp != null)
                    harmony.Patch(checkXp, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(CheckXPThreshold_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.CheckXPThreshold not found — bug1 downed guard disabled.");

                // Bug-2 (enemies ignore the client): Endless enemies use overridetargets (RefreshTargets) which only lists
                // the host player, so the client is never a candidate. Re-add the client ghost units after each refresh.
                ResolveTargetingReflection(emType);
                var refreshTargets = AccessTools.DeclaredMethod(emType, "RefreshTargets");
                if (refreshTargets != null)
                    harmony.Patch(refreshTargets, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(RefreshTargets_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.RefreshTargets not found — client targeting fix disabled.");

                // EM-6b-2 (Shared mode 3D card mirror via roll-state replay):
                //  - host captures the pre-roll RNG + selection state (FloatingCardManager.SpawnCards prefix);
                //  - client forces ChoiceDrawAmount to the host's value so the card count matches (getter postfix);
                //  - client blocks its own card pick while replaying a shared roll (the pick becomes a vote in EM-6b-3).
                var fcmType = AccessTools.TypeByName("FloatingCardManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.FloatingCardManager");
                if (fcmType != null)
                {
                    var spawnCards = AccessTools.Method(fcmType, "SpawnCards", Type.EmptyTypes);
                    if (spawnCards != null)
                        harmony.Patch(spawnCards, prefix: new HarmonyMethod(
                            typeof(EndlessSyncPatches).GetMethod(nameof(SpawnCards_Pre), BindingFlags.Static | BindingFlags.NonPublic)));

                    foreach (var pickName in new[] { "OnPlayerFired", "OnPlayerInteracted" })
                    {
                        var pick = AccessTools.Method(fcmType, pickName);
                        if (pick != null)
                            harmony.Patch(pick, prefix: new HarmonyMethod(
                                typeof(EndlessSyncPatches).GetMethod(nameof(CardPick_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    }

                    // EM-6b-3a: during the tie-break raffle, drive the native card highlight (border + enlarge) along the
                    // sweep by overriding the frame's aim-based SetSelected — a postfix so it wins after the vanilla loop.
                    var fcmUpdate = AccessTools.Method(fcmType, "Update", Type.EmptyTypes);
                    if (fcmUpdate != null)
                        harmony.Patch(fcmUpdate, postfix: new HarmonyMethod(
                            typeof(EndlessSyncPatches).GetMethod(nameof(CardManagerUpdate_Post), BindingFlags.Static | BindingFlags.NonPublic)));

                    // EM-7b: the card-spawn locator beam (loot / chest / NPC) is host-authoritative — a linked client
                    // suppresses its own local activation so the beam is driven purely by the host wave-state snapshot
                    // (spawn + move + disappear in sync at the host's positions). Host + single-player run vanilla.
                    var lootBeam = AccessTools.Method(fcmType, "SpawnLootLightEffect", new[] { typeof(UnityEngine.Vector3) });
                    if (lootBeam != null)
                        harmony.Patch(lootBeam, prefix: new HarmonyMethod(
                            typeof(EndlessSyncPatches).GetMethod(nameof(SpawnLootLightEffect_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    else Plugin.Log.Info("[Endless] FloatingCardManager.SpawnLootLightEffect not found — EM-7b beam sync disabled.");
                }
                else Plugin.Log.Info("[Endless] FloatingCardManager not found — EM-6b shared card mirror disabled.");

                // EM-6b-3a: force the per-save storage/service unlock filter open while a shared-mode roll rebuilds its
                // pools (both ends), so those cards' item pools are identical and the roll stays deterministic. Outside
                // that window the real unlock state is used, so nothing else changes.
                var crType = AccessTools.TypeByName("CardReward") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.CardReward");
                var isStorageUnlocked = crType != null ? AccessTools.Method(crType, "IsStorageUnlocked") : null;
                if (isStorageUnlocked != null && isStorageUnlocked.ReturnType == typeof(bool))
                    harmony.Patch(isStorageUnlocked, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(IsStorageUnlocked_Pre), BindingFlags.Static | BindingFlags.NonPublic)));

                var choiceDraw = AccessTools.PropertyGetter(emType, "ChoiceDrawAmount");
                if (choiceDraw != null)
                    harmony.Patch(choiceDraw, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(ChoiceDrawAmount_Post), BindingFlags.Static | BindingFlags.NonPublic)));

                // EM-7 (Shared mode): route Endless card LOOT to a single authority. SpawnFromLootTable's plain-Pickup path
                // (containerPrefab == null) goes through InteractionManager.SpawnPickup, which the WID pipeline already
                // mirrors — so the host spawns it once (tagged EndlessSharedLootContext so it mirrors regardless of the
                // SharedLoot toggle) and the client suppresses its own duplicate (the reported "loot spawns twice" bug).
                // Container-path loot + companions + shop NPCs are later EM-7 slices.
                var execReward = fcmType != null ? AccessTools.Method(fcmType, "ExecuteReward") : null;
                if (execReward != null && crType != null)
                {
                    _crRewardType      = AccessTools.Field(crType, "rewardType");
                    _crContainerPrefab = AccessTools.Field(crType, "containerPrefab");
                    if (_crRewardType != null)
                    {
                        try { _lootTableRewardValue        = Convert.ToInt32(Enum.Parse(_crRewardType.FieldType, "SpawnFromLootTable")); } catch { }
                        try { _spawnRandomAlliesRewardValue = Convert.ToInt32(Enum.Parse(_crRewardType.FieldType, "SpawnRandomAllies")); } catch { }
                        try { _spawnNpcRewardValue          = Convert.ToInt32(Enum.Parse(_crRewardType.FieldType, "SpawnNPC")); } catch { }
                        try { _spawnInteractableRewardValue = Convert.ToInt32(Enum.Parse(_crRewardType.FieldType, "SpawnInteractable")); } catch { }
                    }
                    harmony.Patch(execReward,
                        prefix:  new HarmonyMethod(typeof(EndlessSyncPatches).GetMethod(nameof(ExecuteReward_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(EndlessSyncPatches).GetMethod(nameof(ExecuteReward_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                else Plugin.Log.Info("[Endless] FloatingCardManager.ExecuteReward not found — EM-7 loot routing disabled.");

                // EM-7c: bracket the host's companion spawn so RuntimeSpawnManager mirrors only companion spawns from
                // FloatingCardManager (shop NPCs share the owner type but are handled separately in EM-7d).
                var spawnCompanion = fcmType != null ? AccessTools.Method(fcmType, "SpawnCompanion") : null;
                if (spawnCompanion != null)
                    harmony.Patch(spawnCompanion,
                        prefix:  new HarmonyMethod(typeof(EndlessSyncPatches).GetMethod(nameof(SpawnCompanion_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(EndlessSyncPatches).GetMethod(nameof(SpawnCompanion_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] FloatingCardManager.SpawnCompanion not found — EM-7c companion mirror disabled.");

                // IND-1: redirect the charm target when the HOST spawns a companion on behalf of a client (so it follows
                // the picker's ghost, not the host). Gated by a consume-once flag set only during that on-behalf spawn, so
                // every other ApplyForcedCharmed is untouched. Patch the 2-arg overload SpawnCompanion uses (Unit, bool).
                var npcType2 = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc") ?? AccessTools.TypeByName("Npc");
                MethodInfo? applyCharmed2 = null;
                if (npcType2 != null)
                    foreach (var m in npcType2.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (m.Name == "ApplyForcedCharmed" && m.GetParameters().Length == 2) { applyCharmed2 = m; break; }
                if (applyCharmed2 != null)
                    harmony.Patch(applyCharmed2, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(ApplyForcedCharmed_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] Npc.ApplyForcedCharmed(Unit,bool) not found — IND-1 charm redirect disabled.");

                Plugin.Log.Info($"[Endless] EM-1/EM-5 client slave + progression patched ({patched} core). EM-2 mirror via RuntimeSpawnManager.ClassifyOwner(Endless).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless] EM-1 Apply failed: {ex.Message}"); }
        }

        // Mode-aware client Update.
        //  - SHARED mode: driven slave (EM-3) — skip the vanilla wave state machine + XP-threshold/card triggers (all
        //    host-authoritative) and render the HUD from the host-synced fields.
        //  - INDEPENDENT mode (EM-5): let vanilla Update run so the client drives its OWN XP/leveling/card flow. The
        //    world stays host-authoritative for free — the wave-advance guard `kill% >= 100 && allBurstsSpawnedForCurrentWave`
        //    can never fire on the client because StartEnemySpawning (which alone sets that flag true) is suppressed.
        // Host + single-player always run vanilla.
        private static bool Update_Pre(object __instance)
        {
            if (!Enabled || !IsLinkedClient) return true;
            if (EndlessSyncManager.IsIndependentMode)
            {
                EndlessSyncManager.EnsureInvulnClearedIfNotSelecting(__instance); // safety: drop the bubble once the pick ends
                return true; // run vanilla Update — own progression
            }
            ClientWaveDriverSkipped++;
            EndlessSyncManager.ClientRenderUI(__instance); // EM-3 shared: host-driven HUD; no-op until state resolves
            return false;
        }

        // EM-5b (HOST): the canonical Endless XP source. XP is a host-authoritative shared pickup (not per-player), so
        // the host's own vanilla orb spawn is suppressed (see SuppressVanillaOrbSpawn / SpawnOrb_Pre) and replaced by one
        // broadcast pickup that both ends spawn + collect together. The prefix arms the suppression around the vanilla
        // OnEnemyDied body (which spawns the orbs); the postfix disarms it and mints the pickup.
        internal static bool SuppressVanillaOrbSpawn;

        private static void OnEnemyDied_Pre()
        {
            if (Enabled && NetGameplaySyncBridge.BossMode == NetMode.Host) SuppressVanillaOrbSpawn = true;
        }

        private static void OnEnemyDied_Post(object __instance, object npc, object unitSO)
        {
            try
            {
                SuppressVanillaOrbSpawn = false;
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                int xpOnKill = ReadInt(unitSO, "ExperienceOnKill");
                if (xpOnKill <= 0) return;
                if (!TryReadCorpsePosition(npc, out UnityEngine.Vector3 pos)) return;
                // totalXp + orb count = base ExperienceOnKill (the melee-XP-bonus card is host-personal and not shared).
                if (EndlessSyncManager.IsIndependentMode)
                    EndlessSyncManager.HostAwardXpForKill(npc, pos, xpOnKill); // EM-5c: award to first-damager / last-hit
                else
                {
                    // #2c: scale the shared award by the host's IncreaseXPAmount multiplier (both ends share it in Shared mode).
                    float mult = EndlessSyncManager.HostXpMultiplier;
                    int total = mult > 1.0001f ? UnityEngine.Mathf.RoundToInt(xpOnKill * mult) : xpOnKill;
                    EndlessSyncManager.HostOnEnemyKilled(pos, total, xpOnKill); // Shared: pickup
                }
            }
            catch (Exception ex) { SuppressVanillaOrbSpawn = false; Plugin.Log.Warn($"[Endless] OnEnemyDied_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Swallow the vanilla host-local orb spawn inside OnEnemyDied (they are replaced by the shared pickup). Only fires
        // while the OnEnemyDied bracket is armed on the host; our own visual-orb spawns (xpValue 0) run outside it.
        private static bool SpawnOrb_Pre() => !SuppressVanillaOrbSpawn;

        // EM-5 (BOTH ENDS, Independent mode): the vanilla card-selection freezes the whole game (SetTimeScale 0), which
        // in co-op freezes the shared world for the other player too. In Independent mode the world must keep running, so
        // undo the freeze the instant it starts and give the selecting player a brief local invuln bubble instead.
        private static void StartTransition_Post(object newState)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode == NetMode.Off) return;
                if (!EndlessSyncManager.IsIndependentMode) return;
                if (Convert.ToInt32(newState) != 2 /* TransitionState.CardSelection */) return;
                EndlessSyncManager.UndoCardSelectFreeze();
                EndlessSyncManager.OnIndependentCardSelectOpened();
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] StartTransition_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // EM-5: the card spin finished (a card was picked) — drop the invuln bubble.
        private static void CardSpinComplete_Post()
        {
            try { EndlessSyncManager.ClearCardSelectInvuln(); } catch { }
        }

        // bug1: while the local player is downed, skip the XP-threshold check so card selection can't open (which would
        // soft-lock controls through revive). currentXP is untouched, so it opens normally once the player is revived.
        private static bool CheckXPThreshold_Pre()
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode == NetMode.Off) return true;
                if (NetPlayerLifeManager.IsPeerDowned(NetGameplaySyncBridge.LocalPeerId)) return false;
            }
            catch { }
            return true;
        }

        // EM-6b-2 HOST: capture the card RNG + selection state before the vanilla roll consumes it (Shared mode only; the
        // manager self-gates). EM-6b-3b CLIENT: in Shared mode the only legitimate client card spawn is our host-driven
        // replay (ClientRollActive is set just before we invoke SpawnCards); suppress any other SpawnCards — notably the
        // terminal re-roll inside a resolved Reroll's SpinAndDismissCard — so the client never rolls a divergent panel.
        // The host owns the reroll and broadcasts the new one. Independent mode + single-player are untouched.
        private static bool SpawnCards_Pre(object __instance)
        {
            try
            {
                if (!Enabled) return true;
                if (NetGameplaySyncBridge.BossMode == NetMode.Host) { EndlessSyncManager_HostCaptureRoll(__instance); return true; }
                if (NetGameplaySyncBridge.BossMode == NetMode.Client
                    && !EndlessSyncManager.IsIndependentMode
                    && !EndlessCardManager.ClientRollActive)
                    return false; // suppress non-replay client spawns (reroll re-roll etc.)
            }
            catch { }
            return true;
        }

        private static void EndlessSyncManager_HostCaptureRoll(object fcm) => EndlessCardManager.HostCaptureRoll(fcm);

        // EM-6b-2 CLIENT: force ChoiceDrawAmount to the host's value while replaying a driven roll so the card count matches.
        // Host + single-player are untouched (ClientRollActive is only ever set on a client mid-replay).
        private static void ChoiceDrawAmount_Post(ref int __result)
        {
            try { if (EndlessCardManager.ClientRollActive) __result = EndlessCardManager.ClientChoiceDrawOverride; }
            catch { }
        }

        // EM-7 (Shared mode): a resolved card runs ExecuteReward on BOTH ends (the shared vote applies the pick on each),
        // so a world reward that spawns an object duplicates it. World rewards are routed to a single authority:
        //  - CLIENT (linked, Shared): suppress the world-spawn branch — the host's copy is mirrored in (loot via the WID
        //    pipeline, companions via the RuntimeSpawn puppet pipeline), so running it here is what duplicates the object.
        //  - HOST (Shared): loot only — tag the spawn (EndlessSharedLootContext) so the SpawnPickup mirrors to the client
        //    regardless of the SharedLoot session toggle (cleared in the postfix). Companions need no host tag here (the
        //    SpawnCompanion bracket classifies them for RuntimeSpawn).
        // Independent mode + single-player untouched. Container-path loot, shop NPCs, and interactables are later slices.
        private static bool ExecuteReward_Pre(object reward, ref System.Threading.Tasks.Task __result)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode == NetMode.Off) return true;
                if (EndlessSyncManager.IsIndependentMode) return true;   // Shared mode only (this slice)

                int kind = ClassifyWorldReward(reward);                  // 0 none, 1 loot-Pickup, 2 companion, 3 shop NPC
                if (kind == 0) return true;

                if (IsLinkedClient)
                {
                    __result = System.Threading.Tasks.Task.CompletedTask; // host's copy arrives via mirror — no duplicate
                    return false;
                }
                if (NetGameplaySyncBridge.BossMode == NetMode.Host)
                {
                    if (kind == 1) WorldPickupManager.EndlessSharedLootContext = true; // loot: mirror regardless of SharedLoot toggle
                    if (kind == 3) HostShopSpawnDepth++;                               // shop: bracket the SpawnNPC SpawnUnitAsync
                    if (kind == 4 || kind == 5) EndlessInteractableManager.HostArmCapture(reward); // EM-7e: capture spawned chest/station/container roots
                }
            }
            catch { }
            return true;
        }

        // EM-7b: in Shared mode the card-spawn locator beam is host-authoritative — a linked client suppresses its own
        // local activation so the beam is host-driven via the wave-state snapshot. Independent mode keeps its own local
        // beam (each player's world-card spawns are separate there). Host + single-player run vanilla.
        private static bool SpawnLootLightEffect_Pre()
            => !(Enabled && IsLinkedClient && !EndlessSyncManager.IsIndependentMode);

        // Clear the host world-spawn brackets once ExecuteReward's synchronous portion returns. The loot branch has no await
        // (runs to completion first); the shop branch's first await is its SpawnUnitAsync, which NotePendingSpawn already
        // observed synchronously — so clearing here is correct for both. Non-world branches never set them.
        private static void ExecuteReward_Post()
        {
            try { WorldPickupManager.EndlessSharedLootContext = false; } catch { }
            try { if (HostShopSpawnDepth > 0) HostShopSpawnDepth--; } catch { }
            // EM-7e: the SpawnInteractable branch is synchronous (no await), so by now spawnedInteractables is fully
            // populated — read the new roots and broadcast them. No-op unless capture was armed on the host.
            try { EndlessInteractableManager.HostCaptureAndBroadcast(); } catch { }
        }

        // 0 = not routed; 1 = loot-table plain-Pickup (WID mirror); 2 = SpawnRandomAllies companion; 3 = SpawnNPC shop;
        // 4 = SpawnInteractable (storage / service station); 5 = SpawnFromLootTable Container-path chest.
        // Kinds 4 & 5 share the EM-7e non-unit interactable mirror.
        private static int ClassifyWorldReward(object reward)
        {
            try
            {
                if (reward == null || _crRewardType == null) return 0;
                int rt = Convert.ToInt32(_crRewardType.GetValue(reward));
                if (rt == _lootTableRewardValue && _lootTableRewardValue != int.MinValue)
                {
                    // containerPrefab != null → the Container path (an interactable Instantiate, not a SpawnPickup). This is
                    // the card CHEST the game actually spawns (SpawnFromLootTable + a Container prefab), mirrored like
                    // SpawnInteractable via the non-unit interactable mirror (EM-7e kind 5). containerPrefab == null → plain
                    // Pickup, deduped via the WID pipeline (EM-7a kind 1).
                    if (_crContainerPrefab != null && _crContainerPrefab.GetValue(reward) != null) return 5;
                    return 1;
                }
                if (rt == _spawnRandomAlliesRewardValue && _spawnRandomAlliesRewardValue != int.MinValue) return 2;
                if (rt == _spawnNpcRewardValue          && _spawnNpcRewardValue          != int.MinValue) return 3;
                if (rt == _spawnInteractableRewardValue && _spawnInteractableRewardValue != int.MinValue) return 4; // EM-7e chest / storage / station
                return 0;
            }
            catch { return 0; }
        }

        // EM-7c: bracket the host's companion spawn. SpawnCompanion is async void and calls SpawnUnitAsync synchronously
        // (before its first await), so the depth is >0 exactly when RuntimeSpawnManager.NotePendingSpawn observes the
        // companion's SpawnUnitAsync — classifying only companion spawns from FloatingCardManager for the mirror.
        //
        // IND-1: in Independent mode a linked client's local companion can't fight (its enemies are host-authoritative
        // puppets), so route the pick to the host instead of spawning locally — the host spawns the authoritative
        // companion (charmed to the picker) and it mirrors back as a puppet. Returns false to suppress the local spawn.
        private static bool SpawnCompanion_Pre(object unitSO)
        {
            if (!Enabled) return true;
            if (EndlessSyncManager.IsIndependentMode && IsLinkedClient)
            {
                EndlessWorldCardManager.ClientRouteCompanion(unitSO);
                return false; // don't spawn the non-functional local companion
            }
            if (NetGameplaySyncBridge.BossMode == NetMode.Host) HostCompanionSpawnDepth++;
            return true;
        }

        private static void SpawnCompanion_Post()
        {
            if (Enabled && NetGameplaySyncBridge.BossMode == NetMode.Host && HostCompanionSpawnDepth > 0) HostCompanionSpawnDepth--;
        }

        // IND-1 (HOST): while an on-behalf companion spawn is pending, swap ApplyForcedCharmed's owner (arg 0) to the
        // requesting peer's ghost unit so the companion follows the picker. Consume-once; a no-op for every other charm.
        // Uses __args (writable) so we don't need to reference the game Unit type in a ref parameter.
        private static void ApplyForcedCharmed_Pre(object[] __args)
        {
            try
            {
                object? target = EndlessWorldCardManager.ConsumeOnBehalfCharmTarget();
                if (target != null && __args != null && __args.Length >= 1) __args[0] = target;
            }
            catch { }
        }

        // EM-6b-3a: in Shared mode the card pick is a 1-of-N group vote, not an immediate selection. Both ends route the
        // Fire/Interact into a vote cast (for the ordinary card being aimed at) and the vanilla pick is blocked; the
        // resolved card is applied on both ends by the vote manager (SpinAndDismissCard). Independent mode + single-player
        // are unchanged (each player picks their own card); the 6b-2 client-replay block still applies before the vote
        // snapshot has arrived.
        private static bool CardPick_Pre(object __instance)
        {
            try
            {
                if (EndlessCardVoteManager.SharedVoteActive)
                {
                    EndlessCardVoteManager.OnLocalPickInput(__instance); // cast (or ignore a non-votable aim); never apply vanilla
                    return false;
                }
                return !(NetGameplaySyncBridge.BossMode == NetMode.Client && EndlessCardManager.ClientRollActive);
            }
            catch { return true; }
        }

        // EM-6b-3a: while a tie-break raffle sweeps, override the frame's card highlight to the raffle cursor so the native
        // border + enlarge hops card to card and lands on the winner. No-op otherwise (normal aim highlight runs).
        private static void CardManagerUpdate_Post(object __instance)
        {
            try { if (EndlessCardVoteManager.RaffleActive) EndlessCardManager.ApplyRaffleHighlight(__instance, EndlessCardVoteManager.RaffleCursorIndex); }
            catch { }
        }

        // EM-6b-3a: force storage/service cards' unlock filter open only while a shared roll rebuilds its pools (both ends),
        // so those pools match and the roll is deterministic. Returns real unlock state at all other times.
        private static bool IsStorageUnlocked_Pre(ref bool __result)
        {
            if (EndlessCardManager.ForceStorageUnlocked) { __result = true; return false; }
            return true;
        }

        private static int ReadInt(object obj, string member)
        {
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var t = obj.GetType();
                var p = t.GetProperty(member, bf); if (p != null) return Convert.ToInt32(p.GetValue(obj));
                var f = t.GetField(member, bf);    if (f != null) return Convert.ToInt32(f.GetValue(obj));
            }
            catch { }
            return 0;
        }

        // Corpse position with the same small upward offset vanilla uses for the orb spawn origin.
        private static bool TryReadCorpsePosition(object npc, out UnityEngine.Vector3 pos)
        {
            pos = default;
            try
            {
                if (npc is UnityEngine.Component c && c != null) { pos = c.transform.position + new UnityEngine.Vector3(0f, 1f, 0f); return true; }
            }
            catch { }
            return false;
        }

        // ---- Bug-2: add the client ghost units to Endless enemies' overridetargets ----
        private static FieldInfo? _fAllSpawned, _fSpawnedBoss;   // EndlessModeManager enemy lists
        private static MemberInfo? _npcAiAgent;                  // Npc.AiAgent (property or field)
        private static FieldInfo? _aiOverrideTargets;            // AiAgent.overridetargets
        private static MethodInfo? _otAddUnits;                  // OverrideTarget.AddUnits(List<Unit>, TargetType)
        private static Type? _otUnitListType;                    // List<Unit>
        private static object? _otTargetTypeClosest;             // TargetType.Closest
        private static bool _targetingResolved;

        private static void ResolveTargetingReflection(Type emType)
        {
            if (_targetingResolved) return;
            _targetingResolved = true;
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fAllSpawned  = emType.GetField("allSpawnedUnits", bf);
                _fSpawnedBoss = emType.GetField("spawnedBossUnits", bf);

                var npcType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc") ?? AccessTools.TypeByName("Npc");
                _npcAiAgent = (MemberInfo?)npcType?.GetProperty("AiAgent", bf) ?? npcType?.GetField("AiAgent", bf);

                var aiType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.AiAgent") ?? AccessTools.TypeByName("AiAgent");
                _aiOverrideTargets = aiType?.GetField("overridetargets", bf);
                var otType = _aiOverrideTargets?.FieldType;
                if (otType != null)
                {
                    foreach (var m in otType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "AddUnits") continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 2) continue;
                        _otAddUnits = m; _otUnitListType = ps[0].ParameterType;
                        try { _otTargetTypeClosest = Enum.Parse(ps[1].ParameterType, "Closest"); } catch { _otTargetTypeClosest = Enum.ToObject(ps[1].ParameterType, 0); }
                        break;
                    }
                }
                Plugin.Log.Info($"[Endless] bug-2 targeting resolved allSpawned={_fAllSpawned != null} aiAgent={_npcAiAgent != null} overrideTargets={_aiOverrideTargets != null} addUnits={_otAddUnits != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ResolveTargetingReflection failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // HOST: after RefreshTargets rebuilt each Endless enemy's (host-only) overridetargets, add the client ghost units
        // so GrabHostileUnit can pick whichever player is nearest (enemies far from the host now aggro the client).
        private static void RefreshTargets_Post(object __instance)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (_otAddUnits == null || _otUnitListType == null || _aiOverrideTargets == null || _npcAiAgent == null) return;

                object[] ghosts;
                lock (RemotePlayerRegistryManager.GhostUnitsByPeer)
                {
                    if (RemotePlayerRegistryManager.GhostUnitsByPeer.Count == 0) return;
                    ghosts = new object[RemotePlayerRegistryManager.GhostUnitsByPeer.Count];
                    RemotePlayerRegistryManager.GhostUnitsByPeer.Values.CopyTo(ghosts, 0);
                }

                var ghostList = (IList)Activator.CreateInstance(_otUnitListType)!;
                foreach (var g in ghosts) if (g is UnityEngine.Object go && go != null) ghostList.Add(g);
                if (ghostList.Count == 0) return;

                AddGhostsToEnemies(_fAllSpawned?.GetValue(__instance) as IEnumerable, ghostList);
                AddGhostsToEnemies(_fSpawnedBoss?.GetValue(__instance) as IEnumerable, ghostList);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] RefreshTargets_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void AddGhostsToEnemies(IEnumerable? enemies, IList ghostList)
        {
            if (enemies == null) return;
            foreach (var npc in enemies)
            {
                if (npc is not UnityEngine.Object no || no == null) continue;
                object? ai = _npcAiAgent switch
                {
                    PropertyInfo p => p.GetValue(npc),
                    FieldInfo f => f.GetValue(npc),
                    _ => null,
                };
                if (ai is not UnityEngine.Object aio || aio == null) continue;
                object? ot = _aiOverrideTargets!.GetValue(ai);
                if (ot == null) continue;
                try { _otAddUnits!.Invoke(ot, new object[] { ghostList, _otTargetTypeClosest! }); } catch { }
            }
        }

        // Skip the per-wave burst spawn coroutine on a linked client; hand StartCoroutine a valid (empty) enumerator so
        // the caller doesn't NRE. Enemies come from the host runtime-spawn mirror instead.
        private static bool StartEnemySpawning_Pre(ref IEnumerator __result)
        {
            if (!Enabled || !IsLinkedClient) return true;
            ClientSpawnCoroutineSkipped++;
            if (LogOn) Plugin.Log.Info($"[Endless] EM-1 client suppressed local StartEnemySpawning (#{ClientSpawnCoroutineSkipped}) — mirroring host waves");
            __result = EmptyRoutine();
            return false;
        }

        private static IEnumerator EmptyRoutine() { yield break; }
    }
}
