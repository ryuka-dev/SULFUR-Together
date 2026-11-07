namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>HR-1: which host object a <see cref="NetClientHitRequest"/> is aimed at. Each kind keeps the addressing
    /// its sync manager already owned; the damage inputs and the result are shared.</summary>
    internal enum NetHitTargetKind : byte
    {
        /// <summary>A roster-bound enemy, addressed by host spawn index + unit identifier.</summary>
        Roster = 1,
        /// <summary>A registered boss encounter's role target (EncounterKey + TargetRole).</summary>
        BossRole = 2,
        /// <summary>The Emperor worm's current vulnerable tail section (single target, resolved on the host).</summary>
        EmperorWormTail = 3,
        /// <summary>The Emperor phase-2 spider npc (single target, resolved on the host).</summary>
        EmperorSpider = 4,
        /// <summary>A body the host owns (CG-1b) — addressed like <see cref="Roster"/>, but the unit must be dead.</summary>
        Corpse = 5,
    }

    /// <summary>
    /// Phase 5.3-B / HR-1: Client → Host request to settle one damage component of one of the client's own hits.
    /// "Client reports intent, host owns result": the host validates every field, runs the game's own
    /// <c>Unit.ReceiveDamage</c> on its real unit (<see cref="RemoteHitResolver"/>) and answers the sender alone with a
    /// <see cref="NetHostHitResult"/> carrying the same <see cref="RequestSeq"/>.
    /// </summary>
    internal sealed class NetClientHitRequest
    {
        // Scene context — must match host's current scene.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        /// <summary>Monotonic per client session; the host echoes it in the result.</summary>
        public int    RequestSeq   { get; set; }

        /// <summary>Stamped by the HOST from the transport mapping on arrival — never read off the wire.</summary>
        public string ClientPeerId { get; set; } = "";

        public NetHitTargetKind TargetKind { get; set; } = NetHitTargetKind.Roster;

        // Roster / Corpse target — identified by Host roster spawnIndex, guarded by UnitIdentifier type check.
        public int    TargetHostSpawnIndex  { get; set; } = -1;
        public string TargetUnitIdentifier  { get; set; } = "";

        // BossRole target.
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string RootName     { get; set; } = "";
        public string TargetRole   { get; set; } = "";

        public NetHitInput Hit;

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

        public string ToCompact()
            => $"seq={RequestSeq} kind={TargetKind} idx={TargetHostSpawnIndex} enc={EncounterKey} role={TargetRole} " +
               $"dmg={Hit.RawDamage:F1} type={Hit.DamageType} crit={Hit.CritChance:F2} melee={Hit.Melee} shape=0x{Hit.ShapeId:X4} ghost={Hit.GhostBonus:F1}";
    }
}
