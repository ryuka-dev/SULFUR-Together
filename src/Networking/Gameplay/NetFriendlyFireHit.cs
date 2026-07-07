using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// FF-1: Client → Host friendly-fire hit report ("my player's shot hit player X for D").
    /// Sent only when the session friendly-fire setting is ON and the local classifier positively identified the
    /// damage source as the local player. The host stamps <see cref="SourcePeerId"/> from the authenticated
    /// connection, re-validates the FF setting + victim state, then applies (victim == host) or relays via the
    /// existing PlayerLifeState HostDamageRequest channel (victim == another client). Same trust model as
    /// <see cref="NetClientHitRequest"/>: client reports intent, host owns the result.
    /// </summary>
    internal sealed class NetFriendlyFireHit
    {
        // Scene context — must match the host's current scene.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int    Seq          { get; set; }
        public string SourcePeerId { get; set; } = ""; // stamped by the host from the authenticated peer
        public string VictimPeerId { get; set; } = "";

        public float  Damage        { get; set; }
        public int    DamageTypeInt { get; set; }

        public bool    HasPosition { get; set; }
        public Vector3 Position    { get; set; }

        public float SentAt { get; set; }

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
