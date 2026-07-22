using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase AC (crypt sync phase 4) — host-authoritative crypt challenge outcome + UI.
    /// <para>Host side: broadcast the challenge's completion / failure (keyed by the crypt's world position) and the
    /// singleton <c>CryptUI</c> bar's label. Client side: on completion replay the local <c>CryptChallengeManager</c>'s
    /// <c>onChallengeCompleted</c> UnityEvent (opens the reward room that holds the exit teleport); on failure replay
    /// <c>onChallengeFailed</c> and kill the local player (shared loss); mirror the native bar for a UI the client can
    /// actually see.</para>
    /// </summary>
    internal static class CryptChallengeSyncManager
    {
        private const float MatchEpsilon = 2.0f; // crypt managers are far apart; generous radius absorbs drift

        private static int _seq;
        private static float _lastUiSentAt;
        private const float UiSendInterval = 0.2f; // throttle the per-frame UI string (cosmetic, latest-wins)

        // Guard so applying a mirrored CryptUI update on the client doesn't recurse through the UpdateInfo/TurnOff hooks.
        private static bool _applyingUi;
        public static bool IsApplyingUi => _applyingUi;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableCryptSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogCryptSync.Value; } catch { return false; } } }
        private static bool IsHost => NetGameplaySyncBridge.BossMode == NetMode.Host;

        // ----------------------------------------------------------------- host broadcast (from CryptChallengePatches)

        public static void HostBroadcastOutcome(object manager, bool completed)
        {
            try
            {
                if (!Enabled || !IsHost) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;
                if (!(manager is Component c) || c == null) return;

                NetGameplaySyncBridge.ReportLocalCryptChallengeState(new NetCryptChallengeState
                {
                    Sequence = ++_seq,
                    Phase    = completed ? NetCryptChallengeState.PhaseCompleted : NetCryptChallengeState.PhaseFailed,
                    Position = c.transform.position,
                });
                if (LogOn) NetLogger.Info($"[CryptChallenge] host broadcast {(completed ? "COMPLETED" : "FAILED")} pos={c.transform.position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[CryptChallenge] HostBroadcastOutcome failed: {ex.Message}"); }
        }

        public static void HostBroadcastUi(string info, bool useTimer)
        {
            try
            {
                if (!Enabled || !IsHost || _applyingUi) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;
                float now = Time.realtimeSinceStartup;
                if (now - _lastUiSentAt < UiSendInterval) return;
                _lastUiSentAt = now;

                NetGameplaySyncBridge.ReportLocalCryptChallengeState(new NetCryptChallengeState
                {
                    Sequence = ++_seq,
                    Phase    = NetCryptChallengeState.PhaseUiUpdate,
                    Info     = info ?? "",
                    UseTimer = useTimer,
                });
            }
            catch (Exception ex) { NetLogger.Warn($"[CryptChallenge] HostBroadcastUi failed: {ex.Message}"); }
        }

        public static void HostBroadcastUiClear()
        {
            try
            {
                if (!Enabled || !IsHost || _applyingUi) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;
                NetGameplaySyncBridge.ReportLocalCryptChallengeState(new NetCryptChallengeState
                {
                    Sequence = ++_seq,
                    Phase    = NetCryptChallengeState.PhaseUiClear,
                });
            }
            catch (Exception ex) { NetLogger.Warn($"[CryptChallenge] HostBroadcastUiClear failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- client apply (receiving peer)

        public static void ApplyRemote(NetCryptChallengeState m)
        {
            if (!Enabled || m == null) return;
            try
            {
                switch (m.Phase)
                {
                    case NetCryptChallengeState.PhaseUiUpdate:  ApplyUiUpdate(m); break;
                    case NetCryptChallengeState.PhaseUiClear:   ApplyUiClear();   break;
                    case NetCryptChallengeState.PhaseCompleted: ApplyOutcome(m, completed: true);  break;
                    case NetCryptChallengeState.PhaseFailed:    ApplyOutcome(m, completed: false); break;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[CryptChallenge] ApplyRemote failed: {ex.Message}"); }
        }

        private static void ApplyUiUpdate(NetCryptChallengeState m)
        {
            _applyingUi = true;
            try { CryptUI.UpdateInfo(m.Info ?? "", m.UseTimer); }
            finally { _applyingUi = false; }
        }

        private static void ApplyUiClear()
        {
            _applyingUi = true;
            try { CryptUI.TurnOff(); }
            finally { _applyingUi = false; }
        }

        private static void ApplyOutcome(NetCryptChallengeState m, bool completed)
        {
            CryptChallengeManager? mgr = FindLocalManager(m.Position);
            if (mgr == null)
            {
                if (LogOn) NetLogger.Info($"[CryptChallenge] client no local CryptChallengeManager near {m.Position:F1}");
            }
            else
            {
                // Replay the exact prefab wiring: onChallengeCompleted opens the reward room (with the exit teleport);
                // onChallengeFailed replays the failure consequences.
                try { (completed ? mgr.onChallengeCompleted : mgr.onChallengeFailed)?.Invoke(); }
                catch (Exception ex) { NetLogger.Warn($"[CryptChallenge] client outcome invoke failed: {ex.Message}"); }
            }

            _applyingUi = true;
            try { CryptUI.TurnOff(); }
            finally { _applyingUi = false; }

            if (!completed)
            {
                // Option A (shared loss): the host's failed challenge kills its own player (vanilla OnChallengeFailed);
                // kill the client's player too so a failure is felt by everyone.
                try
                {
                    var pu = StaticInstance<GameManager>.Instance?.PlayerUnit;
                    if (pu != null && pu.UnitState != UnitState.Dead) pu.Die();
                }
                catch (Exception ex) { NetLogger.Warn($"[CryptChallenge] client death on fail failed: {ex.Message}"); }
            }

            if (LogOn) NetLogger.Info($"[CryptChallenge] client applied {(completed ? "COMPLETED" : "FAILED")} near {m.Position:F1} (mgr={(mgr != null)})");
        }

        private static CryptChallengeManager? FindLocalManager(Vector3 key)
        {
            CryptChallengeManager? best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;
            foreach (var mgr in UnityEngine.Object.FindObjectsByType<CryptChallengeManager>(FindObjectsSortMode.None))
            {
                if (mgr == null) continue;
                float sqr = (mgr.transform.position - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = mgr; }
            }
            return best;
        }
    }
}
