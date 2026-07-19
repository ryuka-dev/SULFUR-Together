using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-3: host-authoritative Endless wave/XP/stage state sync. See Docs/EndlessModeSyncPlan.md §4.4.
    ///
    /// <para>The host owns the Endless progression (<c>currentStage / currentWave / currentBurstIndex / loopCount /
    /// transitionState / currentXP / currentCardLevel</c>). Every host tick this reads the live
    /// <c>EndlessModeManager.Instance</c> and, when anything changed (immediately) or on a low-rate keepalive, broadcasts
    /// a <see cref="NetEndlessWaveState"/> snapshot. The client (a slave since EM-1) applies the snapshot to its local
    /// manager fields and renders the vanilla Endless HUD from them (<see cref="ClientRenderUI"/> → the game's own
    /// <c>UpdateUI</c>), so both HUDs agree. The client never advances waves, rolls XP, or triggers card selection itself
    /// — those stay host-driven (card selection presentation is EM-6).</para>
    ///
    /// <para>All reflection is cached and defensive (never throws into game code). All entry points run on the main
    /// thread (host tick from the service tick; client apply from the network dispatch; client render from the
    /// <c>Update</c> prefix).</para>
    /// </summary>
    internal static class EndlessSyncManager
    {
        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }

        public static int HostStateBroadcast;
        public static int ClientStateApplied;
        public static int HostXpDropBroadcast;
        public static int ClientXpOrbsSpawned;

        /// <summary>EM-4/EM-5: the effective Endless progression mode. Shared = host-driven single pool (EM-3/EM-6);
        /// Independent = each player runs its own XP/cards (EM-5).</summary>
        private static bool SharedMode { get { try { return NetSessionSettings.SharedEndlessProgressEnabled; } catch { return true; } } }
        public static bool IsIndependentMode => !SharedMode;

        /// <summary>True while an Endless run is live (manager present + active). Scopes the host's Endless targeting
        /// override (AiAgent.GetTarget 10m host-bias bypass) strictly to Endless enemies.</summary>
        public static bool EndlessActive
        {
            get { try { if (!Enabled) return false; EnsureResolved(); object? m = ResolveInstance(); return m != null && ReadBool(_fActive, m); } catch { return false; } }
        }

        // ---- cached reflection ----
        private static bool _resolved;
        private static Type? _emType;
        private static Type? _transitionEnumType;
        private static PropertyInfo? _instanceProp;   // static EndlessModeManager.Instance
        private static FieldInfo? _fStage, _fWave, _fBurst, _fLoop, _fTransition, _fXP, _fThreshold, _fCardLevel, _fActive;
        private static FieldInfo? _fXpOrbManager;     // EndlessModeManager.xpOrbManager : XPOrbManager
        private static FieldInfo? _fCardManager;      // EndlessModeManager.cardManager : FloatingCardManager
        private static FieldInfo? _fLootLightEffect;  // FloatingCardManager.lootLightEffect : GameObject (EM-7b spawn-locator beam)
        // #2a: infinite-ammo / indestructible expiry fields (the client drives expiry from the synced wave counter).
        private static FieldInfo? _fInfAmmoActive, _fInfAmmoStart, _fIndestructActive, _fIndestructStart, _fDurabilityDur;
        private static PropertyInfo? _pWaveIncludingLoops; // EndlessModeManager.currentWaveIncludingLoops (getter)
        private static FieldInfo? _fPickupRadius;     // XPOrbManager.pickupRadius : float
        private static FieldInfo? _fXpMultiplier;     // XPOrbManager.xpMultiplier : float (#2c IncreaseXPAmount card)
        private static FieldInfo? _fActiveOrbs;       // XPOrbManager.activeOrbs : List<XPOrb>
        private static FieldInfo? _orbStateField;     // XPOrb.state : OrbState
        private static FieldInfo? _orbCollectStartField; // XPOrb.collectStartTime : float
        private static Type?      _orbStateEnumType;  // OrbState (Collecting = 2)
        private static MethodInfo? _updateUI;         // private void UpdateUI()
        private static MethodInfo? _spawnOrb;         // XPOrbManager.SpawnOrb(Vector3, int)
        private static MethodInfo? _setInvulnerable;  // Unit.SetInvulnerable(bool)
        private static MethodInfo? _setTimeScale;     // GameManager.SetTimeScale(float, float)
        private static MethodInfo? _addLock;          // GameManager.AddLock(PlayerLocks, bool)
        private static object? _playerMovementLock;   // GameManager.PlayerLocks.PlayerMovement (movement only — keeps look + card pick)
        private static PropertyInfo? _gmPlayerUnitProp; // GameManager.PlayerUnit
        // EM-Arena: arena-transition mirroring.
        private static FieldInfo? _fArenaPrefabs;        // EndlessModeManager.arenaPrefabs : List<GameObject>
        private static FieldInfo? _fDebugOverrideArena;  // EndlessModeManager.debugOverrideArena : GameObject (routine honours it, skipping the RNG pick)
        private static FieldInfo? _fLastUsedArenaIndex;  // EndlessModeManager.lastUsedArenaIndex : int (avoid-repeat)
        private static MethodInfo? _arenaTransitionRoutine; // EndlessModeManager.ArenaTransitionRoutine() : IEnumerator (client mirror)

        // ---- host throttle / dedup ----
        private const float XpThrottleSeconds   = 0.15f; // continuous XP changes: at most ~6-7 Hz
        private const float KeepaliveSeconds     = 1.0f;  // periodic resend so a late/rejoining client catches up
        private static int   _revision;
        private static bool  _hadInstance;
        private static float _lastSentAt;
        private static int   _lStage = int.MinValue, _lWave, _lBurst, _lLoop, _lCardLevel, _lTransition;
        private static bool  _lBeamActive;            // EM-7b: last-sent loot-locator beam state (host change detection)
        private static Vector3 _lBeamPos;
        private static float _lXP = float.NaN, _lThreshold = float.NaN;
        // EM-Arena (host): authoritative arena-transition, armed by the ArenaTransitionRoutine prefix. _lArenaEventId is the
        // last-sent id (change detection). _weSetOverride guards clearing debugOverrideArena in the InstantiateArena postfix.
        private static int  _pendingArenaEventId;
        private static int  _pendingArenaIndex = -1;
        private static int  _lArenaEventId;
        private static bool _weSetOverride;

        // ---- client apply / staleness ----
        private static string _clientRunKey = "";
        private static int    _clientLastRevision = -1;
        private static int    _clientArenaEventId; // EM-Arena: last arena-transition the client mirrored (monotonic per run)

        public static void Reset()
        {
            _revision = 0; _hadInstance = false; _lastSentAt = 0f;
            _lStage = int.MinValue; _lWave = _lBurst = _lLoop = _lCardLevel = _lTransition = 0;
            _lXP = float.NaN; _lThreshold = float.NaN;
            _pendingArenaEventId = 0; _pendingArenaIndex = -1; _lArenaEventId = 0; _weSetOverride = false; // EM-Arena
            _clientRunKey = ""; _clientLastRevision = -1; _clientArenaEventId = 0;
            ClearPendingDrops(); // drop the previous run's XP pickups
            ClearDamagerTracking(); // EM-5c: drop the previous run's damager attribution + force-pulls
            ClearCardSelectInvuln(); // never carry a card-select invuln bubble across a level change
            ClearSharedPause(); // EM-6a: never carry a shared-mode client pause across a run boundary
            EndlessInteractableManager.Reset(); // EM-7e: drop mirrored chests/stations from the previous run
            ClearWavePuppetTracking(); // corpse cleanup: forget the previous run's wave-enemy puppets
        }

        // ================================================================== host

        /// <summary>HOST: read the live Endless manager and broadcast its state when it changed (or on keepalive).</summary>
        public static void HostTick()
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                EnsureResolved();
                object? mgr = ResolveInstance();
                if (mgr == null) { _hadInstance = false; ClearCardSelectInvuln(); return; }
                if (!_hadInstance)
                {
                    _hadInstance = true; ResetHostBaseline();       // fresh run → force first send
                    _pendingArenaEventId = 0; _pendingArenaIndex = -1; _weSetOverride = false; // EM-Arena: new run starts on its Awake arena
                }

                // Safety (host): the card-select invuln bubble is normally dropped by CardSpinComplete; also clear it here
                // the moment the host leaves CardSelection, so a host can never get stuck invulnerable.
                EnsureInvulnClearedIfNotSelecting(mgr);

                int stage = ReadInt(_fStage, mgr), wave = ReadInt(_fWave, mgr), burst = ReadInt(_fBurst, mgr);
                int loop = ReadInt(_fLoop, mgr), cardLevel = ReadInt(_fCardLevel, mgr);
                int transition = ReadTransition(mgr);
                float xp = ReadFloat(_fXP, mgr), threshold = ReadFloat(_fThreshold, mgr);

                // EM-Arena: the instant the host enters ArenaTransition (routine just started), pick + force the arena and
                // arm the snapshot — well before the routine reaches InstantiateArena — so the client swaps in step. The
                // arm bumps the arena event id, which forces this same tick to send (arenaChanged below).
                if (transition == 4 && _lTransition != 4) HostPickAndArmArena(mgr);

                // EM-7b: the shared card-spawn locator beam (host-owned single pillar) — active state + its position.
                bool beamActive = false; Vector3 beamPos = _lBeamPos;
                var hostBeam = ResolveLootBeam(mgr);
                if (hostBeam != null) { beamActive = hostBeam.activeSelf; if (beamActive) beamPos = hostBeam.transform.position; }
                bool beamChanged = beamActive != _lBeamActive || (beamActive && (beamPos - _lBeamPos).sqrMagnitude > 0.01f);

                bool arenaChanged = _pendingArenaEventId != _lArenaEventId; // EM-Arena: host armed a new arena swap
                bool discreteChanged = stage != _lStage || wave != _lWave || burst != _lBurst || loop != _lLoop
                                       || cardLevel != _lCardLevel || transition != _lTransition;
                bool xpChanged = xp != _lXP || threshold != _lThreshold;
                float now = Time.realtimeSinceStartup;

                bool send = discreteChanged || beamChanged || arenaChanged
                            || (xpChanged && now - _lastSentAt >= XpThrottleSeconds)
                            || (now - _lastSentAt >= KeepaliveSeconds);
                if (!send) return;

                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed))
                { chap = ""; lvl = -1; hasSeed = false; seed = 0; }

                var msg = new NetEndlessWaveState
                {
                    ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                    Revision = ++_revision,
                    CurrentStage = stage, CurrentWave = wave, CurrentBurstIndex = burst, LoopCount = loop,
                    TransitionState = (byte)transition,
                    CurrentXP = xp, NextCardThresholdXP = threshold, CurrentCardLevel = cardLevel,
                    LootBeamActive = beamActive, LootBeamX = beamPos.x, LootBeamY = beamPos.y, LootBeamZ = beamPos.z,
                    ArenaEventId = _pendingArenaEventId, ArenaIndex = _pendingArenaIndex,
                };

                _lStage = stage; _lWave = wave; _lBurst = burst; _lLoop = loop; _lCardLevel = cardLevel;
                _lTransition = transition; _lXP = xp; _lThreshold = threshold; _lastSentAt = now;
                if (arenaChanged && LogOn)
                    Plugin.Log.Info($"[Endless] EM-Arena host broadcast arena event={_pendingArenaEventId} idx={_pendingArenaIndex} (rev={msg.Revision})");
                _lArenaEventId = _pendingArenaEventId;
                if (LogOn && beamChanged)
                    Plugin.Log.Info($"[Endless] EM-7b host beam {(beamActive ? $"ON pos={beamPos}" : "OFF")} (rev={msg.Revision})");
                _lBeamActive = beamActive; _lBeamPos = beamPos;

                HostStateBroadcast++;
                NetGameplaySyncBridge.BroadcastHostEndlessWaveState(msg);
                if (LogOn && discreteChanged)
                    Plugin.Log.Info($"[Endless] EM-3 host state rev={msg.Revision} stage={stage} wave={wave} burst={burst} loop={loop} " +
                                    $"trans={transition} cardLvl={cardLevel} xp={xp:F0}/{threshold:F0}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostTick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void ResetHostBaseline()
        {
            _lStage = int.MinValue; _lWave = _lBurst = _lLoop = _lCardLevel = _lTransition = 0;
            _lXP = float.NaN; _lThreshold = float.NaN; _lastSentAt = 0f;
            _lBeamActive = false; _lBeamPos = Vector3.zero;
            _lArenaEventId = 0; // EM-Arena: re-send the current arena id on the first snapshot of a fresh run
        }

        /// <summary>#2a (CLIENT): the infinite-ammo / indestructible cards expire after
        /// <c>infiniteDurabilityWaveDuration</c> waves, but the vanilla expiry check lives in <c>StartEnemySpawning</c>
        /// which the client suppresses (EM-1) — so the client's flags (read per-player from <c>EndlessModeManager.Instance</c>
        /// when firing / taking durability loss) would never clear. Drive the expiry from the host-synced wave counter on
        /// every applied snapshot. Both modes (each end set the flag via its own <c>ExecuteReward</c>). The grant wave is the
        /// same on both ends (the wave counter is host-synced), so the thresholds match.</summary>
        private static void ApplyClientFlagExpiry(object mgr)
        {
            try
            {
                if (_pWaveIncludingLoops == null || _fDurabilityDur == null) return;
                int cwil = Convert.ToInt32(_pWaveIncludingLoops.GetValue(mgr));
                int dur  = ReadInt(_fDurabilityDur, mgr);
                if (_fInfAmmoActive != null && ReadBool(_fInfAmmoActive, mgr) && cwil >= ReadInt(_fInfAmmoStart, mgr) + dur)
                { SetBool(_fInfAmmoActive, mgr, false); if (LogOn) Plugin.Log.Info("[Endless] #2a client infinite-ammo expired"); }
                if (_fIndestructActive != null && ReadBool(_fIndestructActive, mgr) && cwil >= ReadInt(_fIndestructStart, mgr) + dur)
                { SetBool(_fIndestructActive, mgr, false); if (LogOn) Plugin.Log.Info("[Endless] #2a client indestructible expired"); }
            }
            catch { }
        }

        /// <summary>EM-7b: resolve the shared card-spawn locator beam GameObject
        /// (<c>EndlessModeManager.cardManager.lootLightEffect</c>).</summary>
        private static UnityEngine.GameObject? ResolveLootBeam(object mgr)
        {
            try
            {
                if (_fCardManager == null || _fLootLightEffect == null) return null;
                object? cm = _fCardManager.GetValue(mgr);
                return cm == null ? null : _fLootLightEffect.GetValue(cm) as UnityEngine.GameObject;
            }
            catch { return null; }
        }

        // ================================================================== EM-Arena arena-transition sync
        //
        // The Endless "level" is the in-place arena swap. The host runs vanilla ArenaTransitionRoutine (chooses a new
        // arena prefab via gameplayRandom, destroys the old arena, instantiates the new one, teleports the player). The
        // client's slave manager never runs that routine, and gameplayRandom has long diverged, so the two would end up in
        // different arenas from the second stage on. We make the pick host-authoritative: the host prefix picks the index
        // (host-owned RNG, avoiding a repeat) BEFORE the routine's own pick, forces it via debugOverrideArena, and arms a
        // monotonic ArenaEventId carried on the EM-3 snapshot. The client forces the same prefab and runs the SAME vanilla
        // routine when it sees a new id, so both ends reuse the game's own cleanup/navmesh/teleport with no reimplementation.

        /// <summary>HOST: called from HostTick the moment the manager enters <c>TransitionState.ArenaTransition</c> — i.e.
        /// right when the vanilla routine starts, ~3 s before it reaches its own arena pick / InstantiateArena. Choose the
        /// next arena host-authoritatively (avoiding an immediate repeat), force it via <c>debugOverrideArena</c> so the
        /// host's own routine uses it instead of its RNG pick, and arm a monotonic event id for the EM-3 snapshot. Arming
        /// here (at the transition edge) rather than at InstantiateArena (near the end of the routine) means the client
        /// receives the arena in the same snapshot that flips it to ArenaTransition and starts its mirror routine in step,
        /// instead of trailing the host by a whole routine's length. Patching the routine's iterator kickoff prefix does
        /// not fire in this game (Log478), and InstantiateArena fires too late + missed some transitions (Log479), so the
        /// canonical hook is the host-owned transitionState edge.</summary>
        public static void HostPickAndArmArena(object mgr)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                EnsureResolved();
                if (_fArenaPrefabs == null || _fDebugOverrideArena == null || mgr == null) return;
                if (_fArenaPrefabs.GetValue(mgr) is not System.Collections.IList prefabs || prefabs.Count == 0) return;

                int last = _fLastUsedArenaIndex != null ? Convert.ToInt32(_fLastUsedArenaIndex.GetValue(mgr)) : -1;
                int idx = UnityEngine.Random.Range(0, prefabs.Count);
                for (int guard = 0; idx == last && prefabs.Count > 1 && guard < 16; guard++)
                    idx = UnityEngine.Random.Range(0, prefabs.Count); // avoid the same arena twice in a row (like vanilla)

                if (prefabs[idx] is not UnityEngine.Object prefab || prefab == null) return;
                _fDebugOverrideArena.SetValue(mgr, prefab);
                _fLastUsedArenaIndex?.SetValue(mgr, idx);
                _weSetOverride = true;

                _pendingArenaIndex = idx;
                _pendingArenaEventId++;
                if (LogOn) Plugin.Log.Info($"[Endless] EM-Arena host armed arena idx={idx} event={_pendingArenaEventId} (prefab={prefab.name})");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostPickAndArmArena failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Both roles: clear the forced <c>debugOverrideArena</c> we set, from the InstantiateArena postfix, so the
        /// field doesn't stick into a later transition. No-op if we didn't set it (a genuine debug override is left alone).</summary>
        public static void ClearForcedArenaIfSet(object mgr)
        {
            try
            {
                if (!_weSetOverride || _fDebugOverrideArena == null || mgr == null) return;
                _fDebugOverrideArena.SetValue(mgr, null);
                _weSetOverride = false;
            }
            catch { }
        }

        /// <summary>CLIENT: mirror one host arena swap — force the host's arena prefab and run the vanilla
        /// ArenaTransitionRoutine locally (its cleanup / navmesh bake / player teleport / per-player "continue" gate). The
        /// transitionState is set to ArenaTransition first so an Independent-mode client (whose Update runs vanilla) parks
        /// on the no-op ArenaTransition case instead of fighting the swap.</summary>
        private static void ClientRunArenaTransition(object mgr, int arenaIndex)
        {
            try
            {
                if (_fArenaPrefabs == null || _fDebugOverrideArena == null || _arenaTransitionRoutine == null) return;
                if (mgr is not MonoBehaviour mb) return;
                if (_fArenaPrefabs.GetValue(mgr) is not System.Collections.IList prefabs
                    || arenaIndex < 0 || arenaIndex >= prefabs.Count) return;
                if (prefabs[arenaIndex] is not UnityEngine.Object prefab || prefab == null) return;

                _fDebugOverrideArena.SetValue(mgr, prefab);
                _weSetOverride = true;
                SetTransition(mgr, 4); // TransitionState.ArenaTransition — park a vanilla-running (Independent) Update
                if (_arenaTransitionRoutine.Invoke(mgr, null) is System.Collections.IEnumerator routine)
                    mb.StartCoroutine(routine);
                if (LogOn) Plugin.Log.Info($"[Endless] EM-Arena client swap arena idx={arenaIndex} event={_clientArenaEventId} (prefab={prefab.name})");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ClientRunArenaTransition failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== client apply

        /// <summary>CLIENT: apply a host wave-state snapshot to the local slave EndlessModeManager.</summary>
        public static void ApplyHostWaveState(NetEndlessWaveState msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;

                // Run-context validation (ignore a snapshot for a level we're not in), matching the runtime-spawn mirror.
                if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                {
                    if (LogOn) Plugin.Log.Info($"[Endless] EM-3 client drop (run mismatch) msg={msg.ChapterName}:{msg.LevelIndex} local={chap}:{lvl}");
                    return;
                }

                // Staleness: revision is monotonic within a host run; a new run (different context) resets the baseline.
                string runKey = $"{msg.ChapterName}:{msg.LevelIndex}:{(msg.HasLevelSeed ? msg.LevelSeed : 0)}";
                if (!string.Equals(runKey, _clientRunKey, StringComparison.Ordinal)) { _clientRunKey = runKey; _clientLastRevision = -1; _clientArenaEventId = 0; }
                if (msg.Revision < _clientLastRevision) return;
                _clientLastRevision = msg.Revision;

                EnsureResolved();
                object? mgr = ResolveInstance();
                if (mgr == null) return; // client not in the Endless arena yet

                // World layer (both modes): the stage/wave/burst/loop the shared host-authoritative world is on.
                SetInt(_fStage, mgr, msg.CurrentStage);
                SetInt(_fWave, mgr, msg.CurrentWave);
                SetInt(_fBurst, mgr, msg.CurrentBurstIndex);
                SetInt(_fLoop, mgr, msg.LoopCount);
                SetBool(_fActive, mgr, true); // ensure the UpdateUI guard passes even before the client's StartEndlessMode ran

                ApplyClientFlagExpiry(mgr); // #2a: expire infinite-ammo / indestructible off the synced wave counter

                // EM-Arena (both modes — the arena is host-authoritative in Shared and Independent alike): the host swapped
                // to a new arena. Drive the client's vanilla ArenaTransitionRoutine with the host's forced prefab. Keyed on
                // a monotonic id (idempotent to the latest arena for a late-joining client). Runs before the progression
                // block so a same-snapshot transitionState=ArenaTransition doesn't matter (we set it in the swap anyway).
                if (msg.ArenaEventId != _clientArenaEventId)
                {
                    _clientArenaEventId = msg.ArenaEventId;
                    if (msg.ArenaEventId > 0 && msg.ArenaIndex >= 0) ClientRunArenaTransition(mgr, msg.ArenaIndex);
                }

                // Progression layer: Shared mode mirrors the host's single pool (EM-3). Independent mode leaves the
                // client's own currentXP / level / card flow untouched (EM-5) — those are driven locally.
                if (SharedMode)
                {
                    SetInt(_fCardLevel, mgr, msg.CurrentCardLevel);
                    SetTransition(mgr, msg.TransitionState);
                    SetFloat(_fXP, mgr, msg.CurrentXP);
                    SetFloat(_fThreshold, mgr, msg.NextCardThresholdXP);
                    ApplySharedPauseEdge(msg.TransitionState); // EM-6a: freeze/resume with the host's shared card-select window
                    // EM-7e: the client's slave manager never runs ArenaTransitionRoutine (its Update is suppressed), so it
                    // can't clean up mirrored chests/stations on a stage change — drive that from the host-synced transition.
                    EndlessInteractableManager.OnClientTransition(msg.TransitionState);
                }

                // EM-7b: mirror the host's card-spawn locator beam (Shared mode only — world-card spawns are
                // host-authoritative there, and the client's own SpawnLootLightEffect is suppressed, so this host-driven
                // state is the client beam's only driver). Independent mode keeps each player's local beam.
                if (SharedMode)
                {
                    var beam = ResolveLootBeam(mgr);
                    if (beam != null)
                    {
                        bool was = beam.activeSelf;
                        if (msg.LootBeamActive)
                        {
                            beam.transform.position = new Vector3(msg.LootBeamX, msg.LootBeamY, msg.LootBeamZ);
                            if (!beam.activeSelf) beam.SetActive(true);
                        }
                        else if (beam.activeSelf) beam.SetActive(false);
                        if (LogOn && was != beam.activeSelf)
                            Plugin.Log.Info($"[Endless] EM-7b client beam {(beam.activeSelf ? $"ON pos=({msg.LootBeamX:F1},{msg.LootBeamY:F1},{msg.LootBeamZ:F1})" : "OFF")} (rev={msg.Revision})");
                    }
                    else if (LogOn && msg.LootBeamActive) Plugin.Log.Info("[Endless] EM-7b client beam: lootLightEffect unresolved (cardManager/field null)");
                }

                ClientStateApplied++;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ApplyHostWaveState failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== client render (from the Update prefix)

        /// <summary>CLIENT: render the vanilla Endless HUD from the host-synced fields (the slave's replacement for its
        /// frozen <c>Update</c>). Skips card-selection / arena-transition frames like vanilla; never advances state.</summary>
        public static void ClientRenderUI(object manager)
        {
            try
            {
                if (manager == null || _updateUI == null) return;
                int transition = ReadTransition(manager);
                // TransitionState: 2 = CardSelection, 4 = ArenaTransition — vanilla suppresses UI updates during these.
                if (transition == 2 || transition == 4) return;
                if (!ReadBool(_fActive, manager)) return;
                _updateUI.Invoke(manager, null);
            }
            catch { /* never break the client frame on a render probe */ }
        }

        // ================================================================== EM-5b host-authoritative XP pickups
        //
        // XP orbs are shared world objects (like WID drops), not per-player: the host assigns a DropId per enemy death
        // and broadcasts it; both ends spawn the same cosmetic orbs (worth 0 — vanilla collection never credits) and
        // register a pending pickup. When a player walks within range, that end asks the host to collect it; the host
        // resolves first-collector-wins and broadcasts the result; both ends remove the orbs together. Only the reward
        // differs by mode — Independent: the collector's local pool; Shared: the host's single pool (mirrored via EM-3).

        private sealed class PendingDrop { public int Id; public Vector3 Pos; public int TotalXp; public int OrbCount; public bool Requested; }
        private static readonly System.Collections.Generic.List<PendingDrop> _pendingDrops = new System.Collections.Generic.List<PendingDrop>();
        private static int _nextDropId;
        private static readonly System.Collections.Generic.HashSet<int> _hostCollected = new System.Collections.Generic.HashSet<int>();

        /// <summary>HOST: an Endless enemy died (canonical <c>OnEnemyDied</c>) — mint a pickup, broadcast it, and register
        /// it locally so the host sees the same orbs. Vanilla's own orb spawn is suppressed (EndlessSyncPatches) so this
        /// is the single XP source.</summary>
        public static void HostOnEnemyKilled(Vector3 position, int totalXp, int orbCount)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (totalXp <= 0) return;
                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed))
                { chap = ""; lvl = -1; hasSeed = false; seed = 0; }

                var msg = new NetEndlessXpDrop
                {
                    ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                    DropId = ++_nextDropId, Position = position, TotalXp = totalXp, OrbCount = Mathf.Clamp(orbCount, 0, 256),
                };
                HostXpDropBroadcast++;
                NetGameplaySyncBridge.BroadcastHostEndlessXpDrop(msg);
                ApplyDrop(msg); // host spawns its own orbs + registers the pending pickup too
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostOnEnemyKilled failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>BOTH ENDS: apply an XP drop. Shared-mode drop (empty AwardPeerId) = spawn cosmetic orbs + register a
        /// pending pickup. Independent-mode award (AwardPeerId set) = only the awardee spawns real orbs that fly straight
        /// to them (force-collected), and vanilla credits the XP as each is absorbed.</summary>
        public static void ApplyDrop(NetEndlessXpDrop msg)
        {
            try
            {
                if (!Enabled || msg == null) return;
                if (NetGameplaySyncBridge.BossMode == NetMode.Client
                    && NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                    return; // different level

                EnsureResolved();

                // Independent-mode award: only the awardee spawns (real-value) orbs that fly straight to them.
                if (!string.IsNullOrEmpty(msg.AwardPeerId))
                {
                    if (!string.Equals(msg.AwardPeerId, NetGameplaySyncBridge.LocalPeerId, StringComparison.Ordinal)) return;
                    object? orbMgr2 = ResolveOrbManager();
                    if (orbMgr2 != null && _spawnOrb != null)
                    {
                        int cnt = Mathf.Clamp(msg.OrbCount, 0, 256);
                        for (int i = 0; i < cnt; i++)
                            _spawnOrb.Invoke(orbMgr2, new object[] { msg.Position, 1 }); // real value → vanilla credits on absorption
                        RegisterForceCollect(msg.Position, cnt); // fly straight to me regardless of distance (snipes included)
                        ClientXpOrbsSpawned += cnt;
                    }
                    return;
                }

                // Shared-mode pickup: cosmetic orbs (0) + a pending pickup collected via first-wins.
                object? orbMgr = ResolveOrbManager();
                if (orbMgr != null && _spawnOrb != null)
                {
                    int count = Mathf.Clamp(msg.OrbCount, 0, 256);
                    for (int i = 0; i < count; i++)
                        _spawnOrb.Invoke(orbMgr, new object[] { msg.Position, 0 }); // xpValue 0 → purely visual
                    ClientXpOrbsSpawned += count;
                }
                lock (_pendingDrops)
                {
                    for (int i = 0; i < _pendingDrops.Count; i++) if (_pendingDrops[i].Id == msg.DropId) return; // dup
                    _pendingDrops.Add(new PendingDrop { Id = msg.DropId, Pos = msg.Position, TotalXp = msg.TotalXp, OrbCount = Mathf.Clamp(msg.OrbCount, 0, 256) });
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ApplyDrop failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- EM-5c: first-damage/last-hit attribution + award

        // Per-enemy damager tracking (host, Independent mode). first = first peer to damage it; last = most recent.
        private static readonly System.Collections.Generic.Dictionary<int, string> _firstDamager = new System.Collections.Generic.Dictionary<int, string>();
        private static readonly System.Collections.Generic.Dictionary<int, string> _lastDamager  = new System.Collections.Generic.Dictionary<int, string>();

        /// <summary>Set by the client-hit handler while it applies a client's forwarded damage, so the host-side damage
        /// hook attributes that damage to the client peer rather than the host.</summary>
        public static string? HostApplyingHitPeer;

        /// <summary>HOST: record who damaged an Endless enemy (from the ReceiveDamage host hook). Only tracks in Independent
        /// mode (Shared uses the pickup path). <see cref="HostApplyingHitPeer"/> attributes client-forwarded damage.</summary>
        public static void RecordHostSideDamager(object npc)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || SharedMode) return;
                int idx = NetGameplayProbeManager.GetSpawnIndexForObject(npc);
                RecordEnemyDamager(idx, HostApplyingHitPeer ?? NetGameplaySyncBridge.LocalPeerId);
            }
            catch { }
        }

        /// <summary>HOST: record (spawnIndex → peer) as first (if absent) + last (always). Called for host hits (from the
        /// ReceiveDamage hook) and directly for client hits (from the client-hit handler, robust to whether the forwarded
        /// damage re-enters the ReceiveDamage hook).</summary>
        public static void RecordEnemyDamager(int spawnIndex, string peer)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || SharedMode) return;
                if (spawnIndex <= 0 || string.IsNullOrEmpty(peer)) return;
                if (!_firstDamager.ContainsKey(spawnIndex)) _firstDamager[spawnIndex] = peer; // first-wins
                _lastDamager[spawnIndex] = peer;                                              // last-hit overwrites
            }
            catch { }
        }

        /// <summary>HOST: an Endless enemy died in Independent mode — resolve its attributed peer (first-damage or last-hit
        /// per the session setting) and broadcast the award. Suppresses nothing here; the vanilla host orb spawn is already
        /// swallowed (EndlessSyncPatches).</summary>
        public static void HostAwardXpForKill(object npc, Vector3 position, int totalXp)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || totalXp <= 0) return;
                int idx = NetGameplayProbeManager.GetSpawnIndexForObject(npc);
                string peer = "";
                if (idx > 0)
                {
                    bool firstDamage = false;
                    try { firstDamage = NetSessionSettings.EndlessXpFirstDamageEnabled; } catch { }
                    var map = firstDamage ? _firstDamager : _lastDamager;
                    map.TryGetValue(idx, out peer);
                    _firstDamager.Remove(idx); _lastDamager.Remove(idx);
                }
                if (string.IsNullOrEmpty(peer)) peer = NetGameplaySyncBridge.LocalPeerId; // no recorded damager → host

                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed))
                { chap = ""; lvl = -1; hasSeed = false; seed = 0; }

                var msg = new NetEndlessXpDrop
                {
                    ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                    DropId = ++_nextDropId, Position = position, TotalXp = totalXp, OrbCount = Mathf.Clamp(totalXp, 0, 256),
                    AwardPeerId = peer,
                };
                HostXpDropBroadcast++;
                NetGameplaySyncBridge.BroadcastHostEndlessXpDrop(msg);
                ApplyDrop(msg); // host applies it too (spawns+force-collects if the host is the awardee)
                if (LogOn) Plugin.Log.Info($"[Endless] EM-5c award kill idx={idx} -> peer={peer} xp={totalXp} ({(NetSessionSettings.EndlessXpFirstDamageEnabled ? "first" : "last")})");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostAwardXpForKill failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---- force-collect: make freshly-spawned award orbs fly straight to the local player from any distance ----
        private sealed class ForcePull { public Vector3 Pos; public int Remaining; public float Deadline; }
        private static readonly System.Collections.Generic.List<ForcePull> _forcePulls = new System.Collections.Generic.List<ForcePull>();

        private static void RegisterForceCollect(Vector3 pos, int count)
        {
            if (count <= 0) return;
            lock (_forcePulls) _forcePulls.Add(new ForcePull { Pos = pos, Remaining = count, Deadline = Time.realtimeSinceStartup + 3f });
        }

        /// <summary>True when the reflection needed to flip orbs into the Collecting state is resolved. If not, callers must
        /// fall back to instant removal so cosmetic orbs never linger.</summary>
        private static bool CanForceCollect() => _fActiveOrbs != null && _orbStateField != null && _orbStateEnumType != null;

        /// <summary>Each frame (service tick): flip freshly-spawned award orbs near a pending pull into the Collecting state
        /// so they home to the local player regardless of distance. Runs until the expected count is pulled or it times out.</summary>
        public static void ForceCollectTick()
        {
            try
            {
                bool any; lock (_forcePulls) any = _forcePulls.Count > 0;
                if (!any) return;
                EnsureResolved();
                object? orbMgr = ResolveOrbManager();
                if (orbMgr == null || _fActiveOrbs == null || _orbStateField == null || _orbStateEnumType == null) return;
                if (_fActiveOrbs.GetValue(orbMgr) is not System.Collections.IList list) return;

                float now = Time.realtimeSinceStartup; // pull deadline clock (matches RegisterForceCollect)
                float gameNow = Time.time;             // vanilla's Collecting math measures collectStartTime against Time.time
                lock (_forcePulls)
                {
                    for (int p = _forcePulls.Count - 1; p >= 0; p--)
                    {
                        var pull = _forcePulls[p];
                        float rSq = OrbRemoveRadius * OrbRemoveRadius;
                        for (int i = 0; i < list.Count && pull.Remaining > 0; i++)
                        {
                            if (list[i] is not Component c || c == null) continue;
                            int state = 0; try { state = Convert.ToInt32(_orbStateField.GetValue(list[i])); } catch { }
                            // Only pull orbs that have finished their spawn burst (Idle=1); leave Spawning(0) orbs to play
                            // the vanilla scatter-arc first (so far kills show the same "burst then fly" as near ones), and
                            // skip Collecting(2) orbs already flying. Near kills never reach here — vanilla collects them
                            // during Spawning while the player is in pickup range.
                            if (state != 1) continue;
                            if ((c.transform.position - pull.Pos).sqrMagnitude > rSq) continue;
                            try { _orbStateField.SetValue(list[i], Enum.ToObject(_orbStateEnumType!, 2)); } catch { }
                            // collectStartTime MUST be Time.time (not realtimeSinceStartup): vanilla's attract-speed ramp
                            // is `Time.time - collectStartTime`; a realtime stamp makes that term huge-negative → the orb
                            // accelerates backwards, shrinks into the distance, and is never captured/credited.
                            try { _orbCollectStartField?.SetValue(list[i], gameNow); } catch { }
                            pull.Remaining--;
                        }
                        if (pull.Remaining <= 0 || now > pull.Deadline) _forcePulls.RemoveAt(p);
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ForceCollectTick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void ClearDamagerTracking()
        {
            _firstDamager.Clear(); _lastDamager.Clear(); HostApplyingHitPeer = null;
            lock (_forcePulls) _forcePulls.Clear();
        }

        /// <summary>BOTH ENDS: when the local player reaches a pending pickup, ask the host to award it (first-wins). The
        /// host handles its own reach directly. Called each frame from the service tick.</summary>
        public static void PickupTick()
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode == NetMode.Off) return;
                bool any; lock (_pendingDrops) any = _pendingDrops.Count > 0;
                if (!any) return;

                object? player = ResolveLocalPlayerUnit();
                if (player is not Component pc || pc == null) return;
                Vector3 pp = pc.transform.position;
                float reach = ReadPickupRadius() + 1.5f; // orbs scatter ~1.5m around the drop centre
                float reachSq = reach * reach;

                string me = NetGameplaySyncBridge.LocalPeerId;
                PendingDrop[] snapshot; lock (_pendingDrops) snapshot = _pendingDrops.ToArray();
                foreach (var d in snapshot)
                {
                    if (d.Requested) continue;
                    if ((d.Pos - pp).sqrMagnitude > reachSq) continue;
                    d.Requested = true;
                    if (NetGameplaySyncBridge.BossMode == NetMode.Host) HostHandleCollectRequest(d.Id, me);
                    else NetGameplaySyncBridge.SendEndlessXpCollectRequest(d.Id);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] PickupTick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: resolve a collection request (from a client or the host itself), first-collector-wins. Applies
        /// the Shared-mode pool add here, then broadcasts + applies the result everywhere.</summary>
        public static void HostHandleCollectRequest(int dropId, string collectorPeerId)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (!_hostCollected.Add(dropId)) return; // already collected by someone — ignore the loser

                int totalXp = 0;
                lock (_pendingDrops) { foreach (var d in _pendingDrops) if (d.Id == dropId) { totalXp = d.TotalXp; break; } }

                // Shared mode: the host's single pool gains it (EM-3 mirrors currentXP to everyone).
                if (SharedMode) AddToHostPool(totalXp);

                var msg = new NetEndlessXpCollect { DropId = dropId, CollectorPeerId = collectorPeerId ?? "", TotalXp = totalXp };
                NetGameplaySyncBridge.BroadcastHostEndlessXpCollected(msg);
                ApplyCollected(msg);
                if (LogOn) Plugin.Log.Info($"[Endless] EM-5b host collected drop={dropId} by={collectorPeerId} xp={totalXp} mode={(SharedMode ? "shared" : "indep")}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostHandleCollectRequest failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>BOTH ENDS: a pickup was collected — remove it + its orbs everywhere; in Independent mode the collector
        /// gains the XP locally.</summary>
        public static void ApplyCollected(NetEndlessXpCollect msg)
        {
            try
            {
                if (!Enabled || msg == null) return;
                Vector3 pos = default; int orbCount = 0; bool found = false;
                lock (_pendingDrops)
                {
                    for (int i = _pendingDrops.Count - 1; i >= 0; i--)
                        if (_pendingDrops[i].Id == msg.DropId) { pos = _pendingDrops[i].Pos; orbCount = _pendingDrops[i].OrbCount; found = true; _pendingDrops.RemoveAt(i); break; }
                }
                if (!found) return; // unknown / already handled

                // On the collector's OWN end, fly the cosmetic orbs into the local player (visual) rather than snapping them
                // out. PickupTick fires at pickupRadius+1.5 — outside vanilla's own collect range — so without this a distant
                // orb is deleted the instant the player crosses the outer reach and never plays the fly-in that near orbs get
                // for free (vanilla self-collects those while the player is already in pickup range). Force-collect flips them
                // to Collecting so vanilla homes them in and removes them; they are value-0 orbs, so no XP is double-counted.
                bool localIsCollector = string.Equals(msg.CollectorPeerId, NetGameplaySyncBridge.LocalPeerId, StringComparison.Ordinal);
                EnsureResolved();
                if (localIsCollector && CanForceCollect())
                    RegisterForceCollect(pos, orbCount > 0 ? orbCount : 256);
                else
                    RemoveOrbsNear(pos); // remote collector (or force-collect unavailable) → the orbs vanish here

                // Independent mode: only the collector's own pool gains it. (Shared mode was applied on the host pool.)
                if (IsIndependentMode && localIsCollector)
                    AddToLocalXp(msg.TotalXp);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ApplyCollected failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void AddToLocalXp(int amount)
        {
            try
            {
                object? mgr = ResolveInstance();
                if (mgr == null || _fXP == null || amount <= 0) return;
                float cur = ReadFloat(_fXP, mgr);
                SetFloat(_fXP, mgr, cur + amount);
            }
            catch { }
        }

        private static void AddToHostPool(int amount) => AddToLocalXp(amount); // on the host, currentXP IS the shared pool

        private static void ClearPendingDrops() { lock (_pendingDrops) _pendingDrops.Clear(); _hostCollected.Clear(); _nextDropId = 0; }

        private const float OrbRemoveRadius = 3.5f; // idle orbs scatter ~2m around the drop centre; comfortably covers them

        private static object? ResolveOrbManager()
        {
            try
            {
                object? mgr = ResolveInstance();
                if (mgr == null || _fXpOrbManager == null) return null;
                object? orb = _fXpOrbManager.GetValue(mgr);
                if (orb is UnityEngine.Object uo && uo == null) return null;
                return orb;
            }
            catch { return null; }
        }

        private static float ReadPickupRadius()
        {
            try { object? orb = ResolveOrbManager(); if (orb != null && _fPickupRadius != null) return Convert.ToSingle(_fPickupRadius.GetValue(orb)); }
            catch { }
            return 3f;
        }

        /// <summary>#2c: the host's Endless XP multiplier (the IncreaseXPAmount card). Applied to the shared XP award so the
        /// card is meaningful in co-op — our XP path awards base <c>ExperienceOnKill</c>, bypassing vanilla's
        /// <c>orbValue * xpMultiplier</c>. Shared mode only (both ends share the multiplier); the Independent-mode per-killer
        /// multiplier is a documented gap (the host doesn't know a client's multiplier).</summary>
        public static float HostXpMultiplier
        {
            get
            {
                try { object? orb = ResolveOrbManager(); if (orb != null && _fXpMultiplier != null) return Convert.ToSingle(_fXpMultiplier.GetValue(orb)); }
                catch { }
                return 1f;
            }
        }

        /// <summary>Deactivate + drop any active XP orbs clustered around a collected pickup's position, so the orbs vanish
        /// on every end when the pickup is taken (on the collector's end they usually already flew in via vanilla).</summary>
        private static void RemoveOrbsNear(Vector3 pos)
        {
            try
            {
                object? orb = ResolveOrbManager();
                if (orb == null || _fActiveOrbs == null) return;
                if (_fActiveOrbs.GetValue(orb) is System.Collections.IList list)
                {
                    float rSq = OrbRemoveRadius * OrbRemoveRadius;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] is Component c && c != null && (c.transform.position - pos).sqrMagnitude <= rSq)
                        {
                            try { c.gameObject.SetActive(false); } catch { }
                            list.RemoveAt(i);
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] RemoveOrbsNear failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== EM-5 no-freeze card-select invuln bubble

        private static bool _cardInvulnActive;

        private static void SetCardSelectMovementLock(bool locked)
        {
            try
            {
                object? gm = RuntimeSpawnManager.GameManagerInstance();
                if (gm == null || _addLock == null || _playerMovementLock == null) return;
                _addLock.Invoke(gm, new object[] { _playerMovementLock, locked }); // movement only — look + card pick stay free
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] card movement lock failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Override the vanilla card-selection freeze (SetTimeScale 0) straight back to normal speed so the shared
        /// co-op world keeps running. A tiny lerp cleanly supersedes the freeze's in-progress lerp target.</summary>
        public static void UndoCardSelectFreeze()
        {
            try
            {
                object? gm = RuntimeSpawnManager.GameManagerInstance();
                if (gm == null || _setTimeScale == null) return;
                _setTimeScale.Invoke(gm, new object[] { 1f, 0.1f });
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] UndoCardSelectFreeze failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== EM-6a shared-mode pause window (client mirror)
        //
        // In Shared mode the host keeps the vanilla card-select freeze (SetTimeScale 0). The host's transitionState is
        // already synced to the client every wave-state snapshot (discrete changes send immediately), so the client freezes
        // and resumes off that single authoritative signal — no extra message. This is compatible with Phase 5.7-NP: NP
        // only blocks pause *padlocks* (Inventory/Paused/DevTools/Dialog); the card-select freeze is a SetTimeScale change
        // (gameState stays Running), which NP never touches. The 1s wave-state keepalive re-asserts the current transition,
        // so a dropped snapshot self-heals and the client can never get stuck frozen.

        private const int TransitionCardSelection = 2; // TransitionState.CardSelection
        private static int _clientPauseTransition = -1;

        /// <summary>CLIENT (Shared mode): freeze/resume in lock-step with the host's card-select transition edge.</summary>
        private static void ApplySharedPauseEdge(int transition)
        {
            if (transition == _clientPauseTransition) return;
            bool wasSelecting = _clientPauseTransition == TransitionCardSelection;
            bool nowSelecting = transition == TransitionCardSelection;
            _clientPauseTransition = transition;
            if (nowSelecting && !wasSelecting) SetClientTimeScale(0f);      // host opened the shared card panel → pause with it
            else if (!nowSelecting && wasSelecting)
            {
                SetClientTimeScale(1f);              // host left card selection → resume
                EndlessCardManager.TeardownClientCards(); // EM-6b-2: close the replayed 3D card panel when the host's ends
            }
        }

        private static void SetClientTimeScale(float scale)
        {
            try
            {
                object? gm = RuntimeSpawnManager.GameManagerInstance();
                if (gm == null || _setTimeScale == null) return;
                _setTimeScale.Invoke(gm, new object[] { scale, scale <= 0f ? 0.5f : 1.5f }); // mirror vanilla's freeze/resume lerps
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6a shared pause client timeScale→{scale:F0}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] SetClientTimeScale failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Safety: never carry a client pause across a run boundary — resume if mid-freeze, then reset the edge.</summary>
        private static void ClearSharedPause()
        {
            if (_clientPauseTransition == TransitionCardSelection) SetClientTimeScale(1f);
            _clientPauseTransition = -1;
        }

        /// <summary>Independent-mode card selection must not freeze the shared world, so the selecting player instead gets
        /// a brief local invulnerability bubble while the (non-freezing) card panel is up. Set when card selection opens.</summary>
        public static void OnIndependentCardSelectOpened()
        {
            try
            {
                if (_cardInvulnActive) return;
                object? player = ResolveLocalPlayerUnit();
                if (player == null || _setInvulnerable == null) return;
                _setInvulnerable.Invoke(player, new object[] { true });
                SetCardSelectMovementLock(true); // lock movement only; look + card-pick input stay free
                _cardInvulnActive = true;
                // A selecting client tells the host to mark its ghost invulnerable so enemies drop it as a target (req 2).
                // The host's own selecting player is excluded directly by the GetTarget bias-bypass (its PlayerUnit is invuln).
                if (NetGameplaySyncBridge.BossMode == NetMode.Client) NetGameplaySyncBridge.SendEndlessCardSelect(true);
                if (LogOn) Plugin.Log.Info("[Endless] EM-5 independent card select — local invuln + movement lock ON (no world freeze)");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] card-invuln on failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Clear the card-selection invuln bubble (card picked / flow ended). Idempotent.</summary>
        public static void ClearCardSelectInvuln()
        {
            try
            {
                if (!_cardInvulnActive) return;
                object? player = ResolveLocalPlayerUnit();
                if (player != null && _setInvulnerable != null)
                    _setInvulnerable.Invoke(player, new object[] { false });
                SetCardSelectMovementLock(false);
                _cardInvulnActive = false;
                if (NetGameplaySyncBridge.BossMode == NetMode.Client) NetGameplaySyncBridge.SendEndlessCardSelect(false); // release ghost aggro on the host
                if (LogOn) Plugin.Log.Info("[Endless] EM-5 independent card select — local invuln + movement lock OFF");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] card-invuln off failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: a client entered/left Independent-mode card selection — mark/unmark its ghost unit invulnerable so
        /// Endless enemies drop it as a target while it picks (req 2). GrabHostileUnit (used by the GetTarget bias-bypass)
        /// already excludes invulnerable units, so this is the single lever needed. Idempotent; a disconnect destroys the
        /// ghost anyway.</summary>
        public static void SetPeerCardSelectInvuln(string peerId, bool selecting)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || string.IsNullOrEmpty(peerId)) return;
                EnsureResolved();
                object? ghost;
                lock (RemotePlayerRegistryManager.GhostUnitsByPeer)
                    RemotePlayerRegistryManager.GhostUnitsByPeer.TryGetValue(peerId, out ghost);
                if (ghost is UnityEngine.Object uo && uo == null) ghost = null;
                if (ghost != null && _setInvulnerable != null) _setInvulnerable.Invoke(ghost, new object[] { selecting });
                if (LogOn) Plugin.Log.Info($"[Endless] EM-5c peer card-select ghost invuln {peerId}={selecting} (ghost={(ghost != null)})");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] SetPeerCardSelectInvuln failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Safety: clear the invuln bubble if the manager is no longer in CardSelection (card flow ended without a
        /// CardSpinComplete). Cheap; called each frame from the client Update path.</summary>
        public static void EnsureInvulnClearedIfNotSelecting(object manager)
        {
            if (!_cardInvulnActive) return;
            try { if (ReadTransition(manager) != 2) ClearCardSelectInvuln(); } catch { }
        }

        // ================================================================== reflection

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _emType = AccessTools.TypeByName("EndlessModeManager")
                       ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.EndlessModeManager");
                if (_emType == null) { Plugin.Log.Warn("[Endless] EM-3 resolve: EndlessModeManager type not found."); return; }

                _transitionEnumType = AccessTools.TypeByName("TransitionState")
                                    ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.TransitionState");

                _instanceProp = _emType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fStage      = _emType.GetField("currentStage", bf);
                _fWave       = _emType.GetField("currentWave", bf);
                _fBurst      = _emType.GetField("currentBurstIndex", bf);
                _fLoop       = _emType.GetField("loopCount", bf);
                _fTransition = _emType.GetField("transitionState", bf);
                _fXP         = _emType.GetField("currentXP", bf);
                _fThreshold  = _emType.GetField("nextCardThresholdXP", bf);
                _fCardLevel  = _emType.GetField("currentCardLevel", bf);
                _fActive     = _emType.GetField("endlessModeActive", bf);
                _fXpOrbManager = _emType.GetField("xpOrbManager", bf);
                _fCardManager  = _emType.GetField("cardManager", bf);
                var fcmType = AccessTools.TypeByName("FloatingCardManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.FloatingCardManager");
                _fLootLightEffect = fcmType?.GetField("lootLightEffect", bf);
                _fInfAmmoActive    = _emType.GetField("infiniteAmmoActive", bf);
                _fInfAmmoStart     = _emType.GetField("infiniteAmmoStartWave", bf);
                _fIndestructActive = _emType.GetField("indestructibleActive", bf);
                _fIndestructStart  = _emType.GetField("indestructibleStartWave", bf);
                _fDurabilityDur    = _emType.GetField("infiniteDurabilityWaveDuration", bf);
                _pWaveIncludingLoops = _emType.GetProperty("currentWaveIncludingLoops", bf);
                _updateUI    = _emType.GetMethod("UpdateUI", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                // EM-Arena: arena-transition mirroring.
                _fArenaPrefabs        = _emType.GetField("arenaPrefabs", bf);
                _fDebugOverrideArena  = _emType.GetField("debugOverrideArena", bf);
                _fLastUsedArenaIndex  = _emType.GetField("lastUsedArenaIndex", bf);
                _arenaTransitionRoutine = _emType.GetMethod("ArenaTransitionRoutine", bf, null, Type.EmptyTypes, null);

                var orbType = AccessTools.TypeByName("XPOrbManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.XPOrbManager");
                if (orbType != null)
                {
                    _spawnOrb = orbType.GetMethod("SpawnOrb", BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(Vector3), typeof(int) }, null);
                    _fPickupRadius = orbType.GetField("pickupRadius", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _fXpMultiplier = orbType.GetField("xpMultiplier", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _fActiveOrbs   = orbType.GetField("activeOrbs", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                var xpOrbType = AccessTools.TypeByName("XPOrb") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.XPOrb");
                if (xpOrbType != null)
                {
                    _orbStateField        = xpOrbType.GetField("state", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _orbCollectStartField = xpOrbType.GetField("collectStartTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _orbStateEnumType     = _orbStateField?.FieldType;
                }

                var unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit") ?? AccessTools.TypeByName("Unit");
                if (unitType != null)
                    _setInvulnerable = unitType.GetMethod("SetInvulnerable", BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(bool) }, null);

                var gmType = BossReflect.FindType("GameManager", "PerfectRandom.Sulfur.Core.GameManager", "PerfectRandom.Sulfur.Gameplay.GameManager");
                _gmPlayerUnitProp = gmType?.GetProperty("PlayerUnit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _setTimeScale = gmType?.GetMethod("SetTimeScale", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(float), typeof(float) }, null);
                // Card-select movement lock: PlayerLocks.PlayerMovement zeroes locomotion (CalculateMovementDirection) while
                // leaving Camera/look and Weapon (card-pick Fire) free — unlike ModifyControllerLock, which locks everything
                // including look + input and soft-locks the card pick.
                var playerLocksType = gmType?.GetNestedType("PlayerLocks", BindingFlags.Public | BindingFlags.NonPublic);
                if (playerLocksType != null && gmType != null)
                {
                    _addLock = gmType.GetMethod("AddLock", BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { playerLocksType, typeof(bool) }, null);
                    try { _playerMovementLock = Enum.Parse(playerLocksType, "PlayerMovement"); } catch { }
                }

                Plugin.Log.Info($"[Endless] EM-3 resolved fields stage={_fStage != null} wave={_fWave != null} xp={_fXP != null} " +
                                $"trans={_fTransition != null} updateUI={_updateUI != null} instance={_instanceProp != null} " +
                                $"orbMgr={_fXpOrbManager != null} spawnOrb={_spawnOrb != null} setInvuln={_setInvulnerable != null} " +
                                $"activeOrbs={_fActiveOrbs != null} orbState={_orbStateField != null} orbEnum={_orbStateEnumType != null} collectStart={_orbCollectStartField != null} " +
                                $"arenaPrefabs={_fArenaPrefabs != null} debugOverrideArena={_fDebugOverrideArena != null} arenaRoutine={_arenaTransitionRoutine != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-3 EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Shared access to the live EndlessModeManager instance (null if not in an Endless arena). Used by
        /// <see cref="EndlessCardManager"/> so the two managers resolve the same singleton without duplicating reflection.</summary>
        public static object? ResolveEndlessInstance() { EnsureResolved(); return ResolveInstance(); }

        // ================================================================== corpse cleanup (client wave-enemy puppets)
        //
        // Vanilla Endless sinks wave-enemy corpses into the ground to prevent them piling up (perf): each SetEnemy wave
        // enemy registers OnEnemyDied → SinkIntoGroundEndless (companions / shop NPCs / bosses do NOT). On the client the
        // wave enemies are host-mirrored puppets whose slave manager never wired that callback, so their corpses leak and
        // accumulate over a long run. Fix: track the puppets mirrored with RuntimeSpawn source "Endless" (= exactly the
        // SetEnemy wave enemies) and, when one dies on the client, run the same vanilla SinkIntoGroundEndless on it. This
        // is local presentation/perf, per-end (like vanilla — each end sinks its own corpses), never authoritative.
        private static readonly System.Collections.Generic.HashSet<int> _wavePuppetIds = new System.Collections.Generic.HashSet<int>();
        private static MethodInfo? _sinkIntoGroundEndless; // Npc.SinkIntoGroundEndless()
        private static bool _sinkResolved;

        /// <summary>CLIENT: remember a mirrored Endless wave-enemy puppet so its corpse is sunk on death (source "Endless"
        /// = SetEnemy wave enemies only — companions/shop/bosses carry a different source and are excluded).</summary>
        public static void NoteEndlessWavePuppet(object unit)
        {
            try { if (unit is UnityEngine.Object uo && uo != null) lock (_wavePuppetIds) _wavePuppetIds.Add(uo.GetInstanceID()); }
            catch { }
        }

        /// <summary>CLIENT: a puppet died (from the runtime-spawn death replay). If it was a tracked Endless wave enemy,
        /// sink its corpse like vanilla's host-side OnEnemyDied does. Removing from the set makes this at-most-once even if
        /// the death event re-applies.</summary>
        public static void OnClientPuppetDied(object npc)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (npc is not UnityEngine.Object uo || uo == null) return;
                bool tracked; lock (_wavePuppetIds) tracked = _wavePuppetIds.Remove(uo.GetInstanceID());
                if (!tracked) return;

                if (!_sinkResolved)
                {
                    _sinkResolved = true;
                    var npcType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc") ?? AccessTools.TypeByName("Npc");
                    _sinkIntoGroundEndless = npcType?.GetMethod("SinkIntoGroundEndless", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (_sinkIntoGroundEndless == null) Plugin.Log.Info("[Endless] corpse cleanup: Npc.SinkIntoGroundEndless not found — client corpses will persist.");
                }
                _sinkIntoGroundEndless?.Invoke(npc, null);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] OnClientPuppetDied failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void ClearWavePuppetTracking() { lock (_wavePuppetIds) _wavePuppetIds.Clear(); }

        private static object? ResolveInstance()
        {
            try
            {
                if (_instanceProp == null) return null;
                object? v = _instanceProp.GetValue(null);
                // Unity-null check: a destroyed manager compares == null.
                if (v is UnityEngine.Object uo && uo == null) return null;
                return v;
            }
            catch { return null; }
        }

        private static object? ResolveLocalPlayerUnit()
        {
            try
            {
                object? gm = RuntimeSpawnManager.GameManagerInstance();
                if (gm == null || _gmPlayerUnitProp == null) return null;
                object? pu = _gmPlayerUnitProp.GetValue(gm);
                if (pu is UnityEngine.Object uo && uo == null) return null;
                return pu;
            }
            catch { return null; }
        }

        private static int ReadInt(FieldInfo? f, object o)   { try { return f?.GetValue(o) is int v ? v : 0; } catch { return 0; } }
        private static float ReadFloat(FieldInfo? f, object o) { try { return f?.GetValue(o) is float v ? v : 0f; } catch { return 0f; } }
        private static bool ReadBool(FieldInfo? f, object o)  { try { return f?.GetValue(o) is bool v && v; } catch { return false; } }
        private static void SetInt(FieldInfo? f, object o, int v)   { try { f?.SetValue(o, v); } catch { } }
        private static void SetFloat(FieldInfo? f, object o, float v) { try { f?.SetValue(o, v); } catch { } }
        private static void SetBool(FieldInfo? f, object o, bool v)  { try { f?.SetValue(o, v); } catch { } }

        private static int ReadTransition(object o)
        {
            try { object? v = _fTransition?.GetValue(o); return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
        }

        private static void SetTransition(object o, int value)
        {
            try
            {
                if (_fTransition == null) return;
                object boxed = _transitionEnumType != null ? Enum.ToObject(_transitionEnumType, value) : (object)value;
                _fTransition.SetValue(o, boxed);
            }
            catch { }
        }
    }
}
