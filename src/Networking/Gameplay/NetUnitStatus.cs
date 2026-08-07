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
    /// ST-2 / ST-3 (Host → all clients): a negative status effect on a host enemy moved in a direction the client cannot
    /// derive for itself. Sent from the canonical transition point, <c>Unit.OnStatusUpdated</c>, which is the same
    /// callback the game itself uses to drive the effect's visuals.
    /// <para><b>What travels (ST-3).</b> A status STARTED, ENDED, or was RAISED. Decay does not travel: it is per-frame,
    /// and the receiving client runs the same vanilla decay itself once the status is set. The original ST-2 cut carried
    /// only start and end, which silently dropped every stack after the first — with a four-pellet frost weapon the host
    /// went 0→20→40→60→80 and the client saw only the 20, then the enemy died with no ice (Log540: 36 host edges, every
    /// one of them 10.0 or 0.0, not a single intermediate value).</para>
    /// <para><b><see cref="Value"/> is the status as it is RIGHT NOW on the host</b>, read back from
    /// <c>EntityStats</c> at broadcast time — not the <c>newValue</c> the callback was handed. Two reasons, both real:
    /// the frozen-solid branch calls <c>SetStatus(Frozen,100)</c> from <i>inside</i> this callback, so the outer
    /// invocation would otherwise report the pre-shatter 80 <i>after</i> the nested one reported 100 and leave the client
    /// lower than the host; and <c>EntityStats.SetStatus</c> hands the callback its UNCLAMPED argument while storing the
    /// clamped one. Reading the truth makes every message idempotent and order-insensitive, which in turn is what makes
    /// it safe to coalesce raises.</para>
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
        /// <summary>Absolute status value on the host, read back at broadcast time. 0 = the effect ended.</summary>
        public float  Value     { get; set; }

        /// <summary>ST-3 flag bits. A byte rather than a bool so a later terminal can be added without another
        /// protocol bump; unknown bits are ignored by design.</summary>
        public byte   Flags     { get; set; }

        /// <summary>Bit 0 — the host's <c>NegativeEffect_Frozen</c> shattered this unit: its
        /// <c>AttributeEffect.ReApplyEffect</c> roll passed and pinned the status to 100.
        /// <para><b>Why this cannot be inferred from <see cref="Value"/> == 100.</b> The roll is
        /// <c>frozenSolidChance(value) * frozenSolidByHealthChance(healthFraction)</c>, and the health term is a step
        /// that reads 0 above half health. So a status CAN be stacked or clamped to a flat 100 on a healthy enemy
        /// without shattering it, and conversely a host that did shatter is not reproducible by a client re-rolling the
        /// same formula. Whether a unit shatters is host-owned world state, not presentation, so it is stated rather
        /// than re-derived.</para></summary>
        public const byte FlagFrozenSolid = 1 << 0;

        public bool FrozenSolid
        {
            get => (Flags & FlagFrozenSolid) != 0;
            set => Flags = (byte)(value ? (Flags | FlagFrozenSolid) : (Flags & ~FlagFrozenSolid));
        }

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
