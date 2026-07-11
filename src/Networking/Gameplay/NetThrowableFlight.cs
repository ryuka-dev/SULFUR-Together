using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HZ-3 throwable in-flight body sync — one event per throw (at release), so other peers can SEE where a grenade/flask
    /// was thrown, not just the landing effect (HZ-2).
    /// <para>Carries the throwing weapon's <c>ItemId</c> (resolves <c>prefabToThrow</c> the same way HZ-2 does) plus the
    /// launch position and velocity. Receivers spawn a VISUAL-ONLY copy of the thrown body (its <c>Breakable</c> disabled
    /// so it never breaks/damages), give it the launch velocity, and auto-destroy it after a short lifetime. The
    /// authoritative on-break effect still arrives separately via <see cref="NetThrowableEffect"/> at the locked spot.</para>
    /// </summary>
    internal sealed class NetThrowableFlight
    {
        public string PeerId { get; set; } = "";

        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        public int     ItemIdValue { get; set; }
        public Vector3 StartPos    { get; set; }
        public Vector3 Velocity    { get; set; }

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
