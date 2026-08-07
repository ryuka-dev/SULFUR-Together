using UnityEngine;
using SULFURTogether.Networking;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.0.0-B host enemy-death event mirror payload.
    ///
    /// This is a network log/matching payload only. It is not a command to mutate
    /// client gameplay unless a future apply path is implemented and explicitly enabled.
    /// </summary>
    internal sealed class NetGameplayDeathEvent
    {
        public string EventId { get; set; } = "";
        public string SourcePeerId { get; set; } = "host";
        public string ChapterName { get; set; } = "<unknown>";
        public int LevelIndex { get; set; } = -1;
        public bool HasLevelSeed { get; set; }
        public int LevelSeed { get; set; }
        public int SourceRevision { get; set; }
        public int Sequence { get; set; }

        public int SpawnIndex { get; set; }
        public string CandidateKey { get; set; } = "";
        public string LocalInstanceId { get; set; } = "";
        public int UnityInstanceId { get; set; }
        public string TypeName { get; set; } = "";
        public string UnitIdentifier { get; set; } = "";
        public string UnitGlobalId { get; set; } = "";
        public string Category { get; set; } = "Npc";
        public string ActorName { get; set; } = "<unknown>";
        public bool HasPosition { get; set; }
        public Vector3 Position { get; set; }
        public int DamageCount { get; set; }
        public string Source { get; set; } = "";
        public float SentAt { get; set; }

        /// <summary>ST-3c flag bits. A byte so later death-time facts can be added without another protocol bump;
        /// unknown bits are ignored by design.</summary>
        public byte Flags { get; set; }

        /// <summary>Bit 0 — the unit was <c>IsFrozenSolid</c> on the sender at the moment it died.
        /// <para><b>Why this has to be stated rather than derived.</b> <c>IsFrozenSolid</c> is literally
        /// <c>GetStatus(Frozen) &gt;= 100f</c>, an exact threshold, and it does NOT require the shatter — an enemy simply
        /// killed while its frost sits at the cap dies as an ice statue. The host meets that threshold by construction:
        /// the bullet that kills it runs <c>ApplyHitModifiers</c> (frost, clamped to 100) and only then
        /// <c>ReceiveDamage</c>, so the status is exactly 100 when <c>Die()</c> runs. A client cannot reproduce that. It
        /// receives the status and the death as two independent messages and runs the vanilla 10/s decay in between, so
        /// its mirror sits a fraction below the cap and <c>&gt;= 100f</c> fails. The visible result is
        /// <c>UpdatePhysicsEnabling</c> settling the corpse into a ragdoll on the client while the host keeps a rigid,
        /// smashable statue (Log542: 32 units reached the cap, only 2 of them actually shattered, and the other 30 are
        /// exactly this case).</para>
        /// <para>Carried on the DEATH event rather than the status channel on purpose: it is read at one instant, and
        /// travelling in the same message as the death removes the ordering race by construction.</para></summary>
        public const byte FlagFrozenSolid = 1 << 0;

        public bool FrozenSolid
        {
            get => (Flags & FlagFrozenSolid) != 0;
            set => Flags = (byte)(value ? (Flags | FlagFrozenSolid) : (Flags & ~FlagFrozenSolid));
        }

        public string SceneKey => $"{ChapterName}:{LevelIndex}";
        public string SeedText => HasLevelSeed ? LevelSeed.ToString() : "?";
        public string PositionText => HasPosition ? $"({Position.x:F2},{Position.y:F2},{Position.z:F2})" : "(?)";

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!NetSceneName.SameScene(localState.ChapterName, localState.LevelIndex, ChapterName, LevelIndex)) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
                return HasLevelSeed && localState.HasLevelSeed && LevelSeed == localState.LevelSeed;
            return true;
        }

        public string ToCompactString()
        {
            return $"event={EventId} src={SourcePeerId} seq={Sequence} idx={SpawnIndex} candidate={CandidateKey} category={Category} actor={ActorName} pos={PositionText} scene={SceneKey} seed={SeedText} damageCount={DamageCount} source={Source}{(FrozenSolid ? " frozenSolid=True" : "")}";
        }
    }
}
