using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.7-BR in-scene destructible sync — one event per <c>Breakable</c> that breaks on the firing peer.
    /// <para>Destructibles are <c>Units.Breakable : Unit</c> placed deterministically by level generation (the seed is
    /// host-authoritative and synced), so both ends own the same set of breakables at the same spawn positions. The key
    /// is the breakable's SPAWN position (captured at <c>Start</c>, before any physics movement) — stable across ends
    /// even for barrels that roll before breaking.</para>
    /// <para>Receivers find the nearest still-alive local Breakable to that position and call <c>Break()</c>, which plays
    /// the break sound/effects, fires <c>onBreakEvents</c>, cascades to child breakables, spawns loot and destroys the
    /// object — the same vanilla path the firing peer ran. Peer-authoritative EFFECT mirror.</para>
    /// </summary>
    internal sealed class NetBreakableBreak
    {
        // Identity — which player broke it (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (so a receiver in a different level ignores it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // Deterministic spawn-position key of the broken Breakable (world space).
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
