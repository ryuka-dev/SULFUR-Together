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

        // ---- cached reflection ----
        private static bool _resolved;
        private static Type? _emType;
        private static Type? _transitionEnumType;
        private static PropertyInfo? _instanceProp;   // static EndlessModeManager.Instance
        private static FieldInfo? _fStage, _fWave, _fBurst, _fLoop, _fTransition, _fXP, _fThreshold, _fCardLevel, _fActive;
        private static MethodInfo? _updateUI;         // private void UpdateUI()

        // ---- host throttle / dedup ----
        private const float XpThrottleSeconds   = 0.15f; // continuous XP changes: at most ~6-7 Hz
        private const float KeepaliveSeconds     = 1.0f;  // periodic resend so a late/rejoining client catches up
        private static int   _revision;
        private static bool  _hadInstance;
        private static float _lastSentAt;
        private static int   _lStage = int.MinValue, _lWave, _lBurst, _lLoop, _lCardLevel, _lTransition;
        private static float _lXP = float.NaN, _lThreshold = float.NaN;

        // ---- client apply / staleness ----
        private static string _clientRunKey = "";
        private static int    _clientLastRevision = -1;

        public static void Reset()
        {
            _revision = 0; _hadInstance = false; _lastSentAt = 0f;
            _lStage = int.MinValue; _lWave = _lBurst = _lLoop = _lCardLevel = _lTransition = 0;
            _lXP = float.NaN; _lThreshold = float.NaN;
            _clientRunKey = ""; _clientLastRevision = -1;
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
                if (mgr == null) { _hadInstance = false; return; }
                if (!_hadInstance) { _hadInstance = true; ResetHostBaseline(); } // fresh run → force first send

                int stage = ReadInt(_fStage, mgr), wave = ReadInt(_fWave, mgr), burst = ReadInt(_fBurst, mgr);
                int loop = ReadInt(_fLoop, mgr), cardLevel = ReadInt(_fCardLevel, mgr);
                int transition = ReadTransition(mgr);
                float xp = ReadFloat(_fXP, mgr), threshold = ReadFloat(_fThreshold, mgr);

                bool discreteChanged = stage != _lStage || wave != _lWave || burst != _lBurst || loop != _lLoop
                                       || cardLevel != _lCardLevel || transition != _lTransition;
                bool xpChanged = xp != _lXP || threshold != _lThreshold;
                float now = Time.realtimeSinceStartup;

                bool send = discreteChanged
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
                };

                _lStage = stage; _lWave = wave; _lBurst = burst; _lLoop = loop; _lCardLevel = cardLevel;
                _lTransition = transition; _lXP = xp; _lThreshold = threshold; _lastSentAt = now;

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
                if (!string.Equals(runKey, _clientRunKey, StringComparison.Ordinal)) { _clientRunKey = runKey; _clientLastRevision = -1; }
                if (msg.Revision < _clientLastRevision) return;
                _clientLastRevision = msg.Revision;

                EnsureResolved();
                object? mgr = ResolveInstance();
                if (mgr == null) return; // client not in the Endless arena yet

                SetInt(_fStage, mgr, msg.CurrentStage);
                SetInt(_fWave, mgr, msg.CurrentWave);
                SetInt(_fBurst, mgr, msg.CurrentBurstIndex);
                SetInt(_fLoop, mgr, msg.LoopCount);
                SetInt(_fCardLevel, mgr, msg.CurrentCardLevel);
                SetTransition(mgr, msg.TransitionState);
                SetFloat(_fXP, mgr, msg.CurrentXP);
                SetFloat(_fThreshold, mgr, msg.NextCardThresholdXP);
                SetBool(_fActive, mgr, true); // ensure the UpdateUI guard passes even before the client's StartEndlessMode ran

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
                _updateUI    = _emType.GetMethod("UpdateUI", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                Plugin.Log.Info($"[Endless] EM-3 resolved fields stage={_fStage != null} wave={_fWave != null} xp={_fXP != null} " +
                                $"trans={_fTransition != null} updateUI={_updateUI != null} instance={_instanceProp != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-3 EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }

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
