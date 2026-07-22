namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// ST-1 (Client → Host): the local player's attack rolled these on-hit status modifiers (weapon enchantments —
    /// Petrification / Fire / Frost / Poison / Stun / Charm / ...) against a host-bound puppet enemy.
    /// <para>Status application and damage are two INDEPENDENT calls in the game: <c>ProjectileUtilities.ProcessUnitHit</c>
    /// calls <c>Unit.ApplyHitModifiers</c> (→ <c>EntityStats.ModifyStatus</c>) before it calls <c>ReceiveDamage</c>.
    /// The damage half already routes to the host (<see cref="NetClientHitRequest"/>), but the host applies it with a raw
    /// health write and never runs <c>ApplyHitModifiers</c> — so before this channel existed a client's enchantment was
    /// applied ONLY to its local puppet: the client saw the petrified material and the shatter VFX while the host's real
    /// NPC, which owns the movement the puppet mirrors, was never petrified and kept walking.</para>
    /// <para>The client rolls each modifier's <c>procChance</c> locally (consuming exactly the RNG draws the suppressed
    /// vanilla call would have) and forwards the entries that PASSED. The host owns the result: resistances, diminishing
    /// returns and the status cap all live inside its own <c>ModifyStatus</c>.</para>
    /// </summary>
    internal sealed class NetClientUnitStatusRequest
    {
        /// <summary>Max modifiers carried by one hit. <c>FixedList32Bytes&lt;ModifierData&gt;</c> physically holds far
        /// fewer than this; the cap exists so a malformed/hostile packet can't make the host loop.</summary>
        public const int MaxEntries = 4;

        // Scene context — must match the host's current scene.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int    RequestSeq   { get; set; }

        // Target — host roster spawnIndex, guarded by the UnitIdentifier type check (same shape as NetClientHitRequest).
        public int    TargetHostSpawnIndex { get; set; } = -1;
        public string TargetUnitIdentifier { get; set; } = "";

        /// <summary>Raw <c>EntityAttributes</c> ids (the enum is <c>ushort</c>) that passed the local proc roll.</summary>
        public ushort[] Attributes { get; set; } = System.Array.Empty<ushort>();
        /// <summary>Per-entry status amount, parallel to <see cref="Attributes"/>. Host re-validates the range.</summary>
        public float[]  Values     { get; set; } = System.Array.Empty<float>();

        public float SentAt { get; set; }

        public string SceneKey => string.IsNullOrWhiteSpace(ChapterName)
            ? "<unknown>:-1"
            : $"{ChapterName}:{LevelIndex}";

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

    /// <summary>
    /// ST-2 (Host → all clients): a negative status effect on a host enemy crossed an EDGE — it started
    /// (<c>Value &gt; 0</c>) or ended (<c>Value == 0</c>). Sent from the canonical transition point,
    /// <c>Unit.OnStatusUpdated</c>, which is the same callback the game itself uses to drive the effect's visuals.
    /// <para>Only the edges travel. A status decays every frame through the same callback, and streaming that would be
    /// pure spam — the receiving client runs the vanilla effect coroutines itself once the status is set, so it decays
    /// locally and the host's end-edge is the authoritative stop.</para>
    /// <para>The client applies it with <c>EntityStats.SetStatus</c> (absolute write, owner callback ON) so the game plays
    /// the real effect on the puppet. That makes the host the single authority for what every screen shows: it corrects
    /// any locally-applied divergence on the next edge, and it also fixes the converse of the ST-1 bug — before this,
    /// a status the HOST applied was invisible on the client (the puppet stopped moving with no petrified material).</para>
    /// </summary>
    internal sealed class NetHostUnitStatusState
    {
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int    HostSpawnIndex { get; set; }
        public string UnitIdentifier { get; set; } = "";

        /// <summary>Raw <c>EntityAttributes</c> id (enum is <c>ushort</c>).</summary>
        public ushort Attribute { get; set; }
        /// <summary>Absolute status value on the host. 0 = the effect ended.</summary>
        public float  Value     { get; set; }

        public int    Sequence  { get; set; }
        public float  SentAt    { get; set; }

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
