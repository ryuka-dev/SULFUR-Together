using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase DB-1 inter-chunk door (<c>DoorBlocker</c>) open sync — one event per door a player finishes opening.
    /// <para>SULFUR 0.18 places hold-to-open doors between chunks (<c>FinalizeLevelNode</c>, seeded from the
    /// level context), and the door physically blocks the doorway until opened. The hold is driven by the LOCAL
    /// player's interaction only, so a door one player opens stays shut — and impassable — on every other end.
    /// This channel mirrors the open to all ends, keyed by the door's deterministic world position.</para>
    /// <para>Opening is one-way: <c>OnFinishedHolding</c> latches <c>isHoldingFinished</c> and nothing re-locks the
    /// door, so the mirror is idempotent and needs no host arbitration — peer-authoritative EFFECT mirror, same
    /// topology as <see cref="NetGateState"/> and <see cref="NetBreakableBreak"/>.</para>
    /// </summary>
    internal sealed class NetDoorBlockerOpen
    {
        // Identity — which player opened the door (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (a receiver in a different level must ignore it — doors at coincidentally similar positions
        // in another level must not match).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // Deterministic world-position key of the door (level-gen placed; DoorBlocker roots never move).
        public Vector3 Position { get; set; }

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
