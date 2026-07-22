using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase KD (crypt sync) — <c>OpenableDoor</c> open sync — one event per locked/one-way door a player opens.
    /// <para>SULFUR's key doors (the desert crypt entrance, and other locked <c>OpenableDoor</c>s) open off the LOCAL
    /// player's interaction — a <c>KeyStation</c> consuming a key, or an item check — so a door one player opens stays
    /// shut and impassable on every other end. There is exactly one crypt key in the shared world, so without this the
    /// second player can never enter. This channel mirrors the open to all ends, keyed by the door's deterministic
    /// world position (level-gen placed from the shared seed, so both ends own the same door at the same spot).</para>
    /// <para>Opening is one-way for the doors we capture (closeable toggle doors are excluded on the capture side), so
    /// the mirror is idempotent and needs no host arbitration — peer-authoritative EFFECT mirror, same topology as
    /// <see cref="NetDoorBlockerOpen"/>.</para>
    /// </summary>
    internal sealed class NetOpenableDoorOpen
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

        // Deterministic world-position key of the door (level-gen placed; OpenableDoor roots never move).
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
