using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase BGC (Black Guild Cardinal) — the alcove fight's two host-authoritative decisions.
    /// <para>The fight is a room of <c>CardinalHelper</c> enemies parked in alcoves (<c>CardinalSpawn</c>) that
    /// teleport between them. Two of its steps are decided locally in vanilla and therefore diverge in co-op:</para>
    /// <list type="bullet">
    ///   <item><b>Fight start</b> — <c>CardinalFightHelper.StartFight</c> is invoked from scene data (no code caller),
    ///   so it runs on whichever end's player fired the trigger. It is the step that lifts every cardinal's
    ///   invulnerability, so an end that never runs it is left with a room of inert, unkillable cardinals.
    ///   <c>FightStartRequest</c> (client→host) / <c>FightStartCommit</c> (host→all) make one end's trigger start the
    ///   fight on every end.</item>
    ///   <item><b>Teleport</b> — <c>CardinalHelper.ExecuteTeleport</c> picks its destination with
    ///   <c>CardinalFightHelper.getUnoccupiedSpawn()</c>, i.e. the global <c>UnityEngine.Random</c>. The host
    ///   broadcasts the alcove it actually picked (<c>TeleportExecute</c>) plus the wind-up
    ///   (<c>TeleportBegin</c>) so every end replays the same native teleport, with its alcove door and audio,
    ///   instead of rolling its own.</item>
    /// </list>
    /// <para>Identity is by <b>index</b>, not by position: after the seeded cull (BGC-1) both ends hold the same
    /// <c>cardinalObjects</c> list in the same order, and <c>spawnPoints</c> is a serialized array — so
    /// <c>CardinalIndex</c> / <c>SpawnIndex</c> mean the same thing on both ends. The helper itself is keyed by its
    /// world position (deterministic generation), the same key the crypt/gate mirrors use.</para>
    /// </summary>
    internal sealed class NetCardinalFightEvent
    {
        public const byte KindFightStartRequest = 0; // client → host: my player's trigger fired StartFight
        public const byte KindFightStartCommit  = 1; // host → all:    run the real StartFight now
        public const byte KindTeleportBegin     = 2; // host → all:    cardinal N started its teleport wind-up
        public const byte KindTeleportExecute   = 3; // host → all:    cardinal N teleports to spawn S

        // Identity — which end sent it.
        public string PeerId { get; set; } = "";

        // Scene context (a receiver in a different level must ignore it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        public byte Kind { get; set; }

        /// <summary>Deterministic world-position key of the owning <c>CardinalFightHelper</c>.</summary>
        public Vector3 HelperPosition { get; set; }

        /// <summary>Index into the helper's <c>cardinalObjects</c> list (teleport kinds only; -1 otherwise).</summary>
        public int CardinalIndex { get; set; } = -1;

        /// <summary>Index into the helper's <c>spawnPoints</c> array (TeleportExecute only; -1 otherwise).</summary>
        public int SpawnIndex { get; set; } = -1;

        /// <summary>The host's destination world position (TeleportExecute). Carried for validation + as the
        /// fallback when the receiver's <c>SpawnIndex</c> does not resolve, so a mismatched alcove array degrades
        /// to "teleport to the right spot" rather than to a divergent local roll.</summary>
        public Vector3 DestinationPosition { get; set; }

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
