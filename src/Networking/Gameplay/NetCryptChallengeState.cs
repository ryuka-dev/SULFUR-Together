using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase AC (crypt sync phase 4) — host-authoritative crypt challenge OUTCOME + UI mirror.
    /// <para>A linked client runs no crypt challenge (SP blocks its <c>StartChallenge</c>): it only sees the host's
    /// mirrored enemies. So the shared results — the reward room opening on completion, the shared death on failure —
    /// and the on-screen progress bar have to come from the host. The host broadcasts:</para>
    /// <list type="bullet">
    ///   <item><b>Completed</b> / <b>Failed</b>: keyed by the crypt's world position, so the client finds its own local
    ///   <c>CryptChallengeManager</c> and replays that manager's <c>onChallengeCompleted</c> / <c>onChallengeFailed</c>
    ///   UnityEvent — the exact prefab wiring that opens the reward room (which holds the exit teleport) — and on a
    ///   failure also kills the local player (both players share the loss).</item>
    ///   <item><b>UiUpdate</b> / <b>UiClear</b>: the singleton <c>CryptUI</c> bar. The host sends the label string it
    ///   already computed (game-localized) plus the timer flag, so the client renders the native bar with no extra
    ///   localization work.</item>
    /// </list>
    /// </summary>
    internal sealed class NetCryptChallengeState
    {
        public const byte PhaseCompleted = 0;
        public const byte PhaseFailed    = 1;
        public const byte PhaseUiUpdate  = 2;
        public const byte PhaseUiClear   = 3;

        // Identity — which end sent it (stamped by the Host from the source peer). Always the host for this channel.
        public string PeerId { get; set; } = "";

        // Scene context (a receiver in a different level must ignore it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        public byte Phase { get; set; }

        // Outcome (Completed/Failed): deterministic world-position key of the crypt's CryptChallengeManager.
        public Vector3 Position { get; set; }

        // UI (UiUpdate): the host's already-localized challenge label + whether the bar is a timer.
        public string Info     { get; set; } = "";
        public bool   UseTimer { get; set; }

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
            {
                if (!HasLevelSeed || !localState.HasLevelSeed) return false;
                if (localState.LevelSeed != LevelSeed) return false;
            }
            return true;
        }
    }
}
