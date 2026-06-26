using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>Phase 5.4-E: Client -> Host "I want to start this boss" request.</summary>
    internal sealed class NetClientBossStartRequest
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string GraphName    { get; set; } = "";
        public string RootName     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public string ClientPeerId { get; set; } = "";
        public string StartSource  { get; set; } = "";
        public float  SentAt       { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} type={BossType} graph={(string.IsNullOrEmpty(GraphName) ? "?" : GraphName)} run={ChapterName}:{LevelIndex} peer={ClientPeerId} src={StartSource}";
    }

    /// <summary>Phase 5.4-E3: a boss dialog-commit signal. Carries the authoritative fact that "the boss dialog is
    /// done and the fight is committed" so every end finalizes its local dialog and starts exactly once. Used for
    /// dialog-gated bosses (Cousin / Lucia). Both the Client→Host request and Host→Client commit share this shape.</summary>
    internal sealed class NetBossDialogCommit
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string GraphName    { get; set; } = "";
        public string RootName     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public string CommitSource { get; set; } = "";
        public int    Revision     { get; set; }
        public float  Timestamp    { get; set; }
        // Phase PF (Plan B): false = INTRO commit (play the boss intro+dialog, do NOT start the fight); true = FIGHT
        // commit (an in-room player dismissed the dialog -> start the gated fight on every end). Two-phase handshake.
        public bool   IsFightCommit { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} type={BossType} run={ChapterName}:{LevelIndex} src={CommitSource} rev={Revision} phase={(IsFightCommit ? "FIGHT" : "intro")}";
    }

    /// <summary>Phase 5.4-E3: minimal host-authoritative boss phase/state snapshot (Witch and other phase bosses).
    /// This phase carries phase index + a small health/add summary; it is deliberately a skeleton for BossPhase sync.</summary>
    internal sealed class NetBossState
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }

        public int    PhaseIndex   { get; set; }
        public bool   FightStarted { get; set; }
        public bool   IntroFinished { get; set; }
        public bool   HasHealth    { get; set; }
        public float  CurrentHealth { get; set; }
        public float  MaxHealth    { get; set; }
        public int    AliveAdds    { get; set; }
        public int    Revision     { get; set; }
        public float  Timestamp    { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} type={BossType} phase={PhaseIndex} fightStarted={FightStarted} hp={(HasHealth ? $"{CurrentHealth:0}/{MaxHealth:0}" : "?")} adds={AliveAdds} rev={Revision}";
    }

    /// <summary>
    /// Phase 5.4-E4: a host-authoritative record of a boss-owned sub-entity spawned at runtime (CousinArm,
    /// BlackGuildLuciaEye, Witch illusions, ...). These are created AFTER level load by boss phase/mechanic code via
    /// UnitSO.SpawnUnitAsync, independently on each end, so the normal WorldRoster (built at scene stabilization) and
    /// proximity binding cannot match them (Lucia eyes even spawn at the same position). The cross-end-stable key is
    /// (EncounterKey, AddType, SequenceIndex): both ends spawn the Nth add of a type in the same deterministic order.
    /// </summary>
    internal sealed class NetBossDynamicSpawn
    {
        public string EncounterKey  { get; set; } = "";
        public string OwnerBossType { get; set; } = "";
        public string AddType       { get; set; } = "";
        public string AddUnitId     { get; set; } = "";
        public int    SequenceIndex { get; set; }
        public Vector3 Position      { get; set; }
        public int    HostInstanceId { get; set; }
        public int    UnitIdValue    { get; set; }  // Phase 5.5-RT3: UnitSO.id.value (mirror-spawn host-only adds)
        public int    HostSpawnIndex { get; set; }  // Phase 5.5-RT3: host SpawnIndex (bind a bound add to the puppet pipeline)
        public string ChapterName   { get; set; } = "";
        public int    LevelIndex    { get; set; } = -1;
        public bool   HasSeed       { get; set; }
        public int    Seed          { get; set; }
        public int    Revision      { get; set; }
        public float  Timestamp     { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} owner={OwnerBossType} add={AddType} seq={SequenceIndex} unitId={(string.IsNullOrEmpty(AddUnitId) ? "?" : AddUnitId)} pos={Position:F1} inst={HostInstanceId}";
    }

    /// <summary>Phase 5.4-F: a client's boss hit, routed to the Host to apply through the real ReceiveDamage. The
    /// Host resolves (encounter, role) to the real target Unit and damages it natively. Result comes back via the
    /// existing BossState broadcast (health/phase). Roles let one boss expose multiple targets later (main / eye /
    /// arm / illusion); this phase only uses "main".</summary>
    internal sealed class NetClientBossHitRequest
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string RootName     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public string TargetRole   { get; set; } = "main";
        public float  Damage       { get; set; }
        public int    DamageTypeInt { get; set; }
        public int    RequestSeq   { get; set; }
        public string ClientPeerId { get; set; } = "";
        public float  SentAt       { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} type={BossType} role={TargetRole} dmg={Damage:0.0} dtype={DamageTypeInt} run={ChapterName}:{LevelIndex} seq={RequestSeq}";
    }

    /// <summary>Phase 5.4-F2: Host→Client "I accepted a boss hit on this target — play the local hit visual".
    /// Visual feedback only; carries no health (health comes via BossState).</summary>
    internal sealed class NetHostBossHitVisual
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string TargetRole   { get; set; } = "main";
        public string TargetUnitId { get; set; } = "";
        public int    Seq          { get; set; }

        public string ToCompact() => $"key={EncounterKey} role={TargetRole} unitId={(string.IsNullOrEmpty(TargetUnitId) ? "?" : TargetUnitId)} seq={Seq}";
    }

    /// <summary>Phase 5.4-F4: a host-authoritative discrete mechanic event for fixed-point bosses (Cousin's
    /// Submerge / MoveToNewPool / Reappear). The Client mirrors the SAME event + pool position instead of running
    /// its own random pool selection. EventName is the real method name; Position is the chosen pool's appear point.</summary>
    internal sealed class NetBossDiscreteEvent
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string EventName    { get; set; } = "";
        public bool   HasPos       { get; set; }
        public Vector3 Position     { get; set; }
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public int    Seq          { get; set; }

        public string ToCompact() => $"key={EncounterKey} event={EventName} pos={(HasPos ? Position.ToString("F1") : "?")} run={ChapterName}:{LevelIndex} seq={Seq}";
    }

    /// <summary>Phase 5.4-F5: a Client's report that one Lucia eye was defeated locally this eye-cycle. Lucia's eye
    /// phase locks the boss body invulnerable until <c>spawnedEyes.Count==0</c> → <c>RestartPhases</c>. The Client
    /// does NOT decide that unlock: it reports a kill and the Host consumes one of ITS living eyes through the real
    /// death path (<c>owner.Die()</c> → <c>EyeDied</c>), so the vanilla cycle/RestartPhases runs host-authoritatively.
    /// Identity is COUNT/CYCLE, not a per-eye entity map: <see cref="Cycle"/> is the Host-stable eye wave (Lucia's
    /// <c>restartCounter</c> hint) and (<see cref="ClientPeerId"/>,<see cref="ReportSeq"/>) dedups a report.</summary>
    internal sealed class NetLuciaEyeReport
    {
        public string EncounterKey  { get; set; } = "";
        public string BossType      { get; set; } = "";
        public string ChapterName   { get; set; } = "";
        public int    LevelIndex    { get; set; } = -1;
        public bool   HasSeed       { get; set; }
        public int    Seed          { get; set; }
        public int    Cycle         { get; set; }          // client restartCounter hint (NOT a local entity seq)
        public int    LocalRemaining { get; set; } = -1;   // client's local spawnedEyes count after the kill (diag)
        public int    ReportSeq     { get; set; }          // per-client monotonic, for dedup
        public string ClientPeerId  { get; set; } = "";
        public float  SentAt        { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} run={ChapterName}:{LevelIndex} cycle={Cycle} localRemaining={LocalRemaining} seq={ReportSeq} peer={ClientPeerId}";
    }

    /// <summary>Phase 5.4-F5: Host→Client authoritative Lucia eye state. <see cref="LivingEyes"/> is the Host's real
    /// <c>spawnedEyes.Count</c> and <see cref="Cycle"/> is the Host's <c>restartCounter</c>. When LivingEyes hits 0 the
    /// Host's vanilla <c>RestartPhases</c> runs (unlocks the body); the Client mirrors the cycle-complete presentation
    /// (clears residual local eyes, lifts darkness) but never decides the unlock itself.</summary>
    internal sealed class NetLuciaEyeState
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public int    Cycle        { get; set; }
        public int    LivingEyes   { get; set; }
        public int    Revision     { get; set; }
        public float  Timestamp    { get; set; }

        public string ToCompact()
            => $"key={EncounterKey} cycle={Cycle} livingEyes={LivingEyes} rev={Revision}";
    }

    /// <summary>Phase 5.4-F6: Host→Client Lucia terminal death. The Host's bossUnit.onDeath → OnBossDead fired (real
    /// boss death). The Client runs a SAFE local death (real Unit death + boss-end presentation) with the host-only
    /// world results (loot/checkpoint/save) isolated, then stops sending hits / applying state for this encounter.</summary>
    internal sealed class NetLuciaDeath
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public int    Revision     { get; set; }
        public float  Timestamp    { get; set; }

        public string ToCompact() => $"key={EncounterKey} run={ChapterName}:{LevelIndex} rev={Revision}";
    }

    /// <summary>Phase 5.4-G2: Host→Client authoritative Witch phase transition. Witch phases CYCLE (Phase6→Phase1),
    /// so a smaller phase enum does NOT mean an older state. <see cref="PhaseRevision"/> is a Host monotonic counter
    /// incremented on every real ChangePhase; the Client applies a phase iff its revision is newer than the last
    /// applied, regardless of the enum value. The Client never self-advances phases (its local ChangePhase is blocked).</summary>
    internal sealed class NetWitchPhase
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }
        public int    PhaseIndex   { get; set; }
        public int    PhaseRevision { get; set; }
        public bool   FightStarted { get; set; }
        public float  Timestamp    { get; set; }

        public string ToCompact() => $"key={EncounterKey} phase={PhaseIndex} revision={PhaseRevision} fightStarted={FightStarted}";
    }

    /// <summary>Phase 5.4-G5: Host→Client authoritative Witch Phase 2 dome layout, captured at the Host's ShowWitches
    /// Postfix (after the second shuffle). Witch Phase 2 has TWO host-local randoms (SpawnWitches real index + ShowWitches
    /// shuffle), so the real witch ends up at a different dome on each end. The only stable identity is (PhaseRevision +
    /// final dome index). The Client mirrors this: it puts ITS realWitchUnit at <see cref="RealDomeIndex"/> and its
    /// illusions at the other domes, so dome N is real on both ends. No per-entity network id needed.</summary>
    internal sealed class NetWitchP2Manifest
    {
        public string EncounterKey  { get; set; } = "";
        public string BossType      { get; set; } = "";
        public string ChapterName   { get; set; } = "";
        public int    LevelIndex    { get; set; } = -1;
        public bool   HasSeed       { get; set; }
        public int    Seed          { get; set; }
        public int    PhaseRevision { get; set; }  // ties this layout to a specific Phase 2 cycle (anti stale)
        public int    DomeCount     { get; set; }
        public int    RealDomeIndex { get; set; }
        public float  Timestamp     { get; set; }

        public string ToCompact() => $"key={EncounterKey} rev={PhaseRevision} domes={DomeCount} realDome={RealDomeIndex}";
    }

    /// <summary>Phase 5.4-G5: Host→Client Witch Phase 2 hit result. Because the Client routes Phase 2 hits to the Host
    /// (local damage suppressed), the Client's own IllusionTakeDamage never fires — the Host must tell it what to hide.
    /// Kind 0 = a single illusion at <see cref="DomeIndex"/> defeated (hide that dome). Kind 1 = the REAL witch was hit
    /// (hide all illusions; the real one stays until the phase ends).</summary>
    internal sealed class NetWitchP2Result
    {
        public const byte KindIllusionDefeated = 0;
        public const byte KindRealHit = 1;

        public string EncounterKey  { get; set; } = "";
        public int    PhaseRevision { get; set; }
        public int    DomeIndex     { get; set; }
        public byte   Kind          { get; set; }

        public string ToCompact() => $"key={EncounterKey} rev={PhaseRevision} dome={DomeIndex} kind={(Kind == KindRealHit ? "realHit" : "illusionDefeated")}";
    }

    /// <summary>Phase 5.4-E: codecs for the boss-encounter messages. Versioned for forward compatibility.</summary>
    internal static class NetBossEncounterCodec
    {
        private const byte RequestVersion = 1;
        private const byte StateVersion = 1;
        private const byte DialogCommitVersion = 2;
        private const byte BossStateVersion = 1;
        private const byte DynamicSpawnVersion = 1;
        private const byte BossHitVersion = 1;
        private const byte BossHitVisualVersion = 1;
        private const byte DiscreteEventVersion = 1;
        private const byte LuciaEyeReportVersion = 1;
        private const byte LuciaEyeStateVersion = 1;
        private const byte LuciaDeathVersion = 1;
        private const byte WitchPhaseVersion = 1;
        private const byte WitchP2ManifestVersion = 1;
        private const byte WitchP2ResultVersion = 1;

        public static void WriteRequest(NetDataWriter w, NetClientBossStartRequest r)
        {
            w.Put(RequestVersion);
            w.Put(r.EncounterKey ?? "");
            w.Put(r.BossType ?? "");
            w.Put(r.GraphName ?? "");
            w.Put(r.RootName ?? "");
            w.Put(r.ChapterName ?? "");
            w.Put(r.LevelIndex);
            w.Put(r.HasSeed);
            if (r.HasSeed) w.Put(r.Seed);
            w.Put(r.ClientPeerId ?? "");
            w.Put(r.StartSource ?? "");
            w.Put(r.SentAt);
        }

        public static bool TryReadRequest(NetDataReader r, out NetClientBossStartRequest result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != RequestVersion) return false;
                var x = new NetClientBossStartRequest
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    GraphName = r.GetString(),
                    RootName = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (x.HasSeed) x.Seed = r.GetInt();
                x.ClientPeerId = r.GetString();
                x.StartSource = r.GetString();
                x.SentAt = r.GetFloat();
                result = x;
                return true;
            }
            catch { return false; }
        }

        public static void WriteState(NetDataWriter w, NetBossEncounterState s)
        {
            w.Put(StateVersion);
            w.Put(s.EncounterKey ?? "");
            w.Put(s.BossType ?? "");
            w.Put(s.GraphName ?? "");
            w.Put(s.RootName ?? "");
            w.Put(s.ChapterName ?? "");
            w.Put(s.LevelIndex);
            w.Put(s.HasSeed);
            if (s.HasSeed) w.Put(s.Seed);
            w.Put(s.Started);
            w.Put(s.StartSource ?? "");
            w.Put(s.HostRevision);
            w.Put(s.HostTimestamp);
            w.Put(s.HasPosition);
            if (s.HasPosition) { w.Put(s.Position.x); w.Put(s.Position.y); w.Put(s.Position.z); }
            w.Put(s.HasPhaseIndex);
            if (s.HasPhaseIndex) w.Put(s.PhaseIndex);
        }

        public static bool TryReadState(NetDataReader r, out NetBossEncounterState result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != StateVersion) return false;
                var s = new NetBossEncounterState
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    GraphName = r.GetString(),
                    RootName = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (s.HasSeed) s.Seed = r.GetInt();
                s.Started = r.GetBool();
                s.StartSource = r.GetString();
                s.HostRevision = r.GetInt();
                s.HostTimestamp = r.GetFloat();
                s.HasPosition = r.GetBool();
                if (s.HasPosition) s.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                s.HasPhaseIndex = r.GetBool();
                if (s.HasPhaseIndex) s.PhaseIndex = r.GetInt();
                result = s;
                return true;
            }
            catch { return false; }
        }

        public static void WriteDialogCommit(NetDataWriter w, NetBossDialogCommit m)
        {
            w.Put(DialogCommitVersion);
            w.Put(m.EncounterKey ?? "");
            w.Put(m.BossType ?? "");
            w.Put(m.GraphName ?? "");
            w.Put(m.RootName ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
            w.Put(m.CommitSource ?? "");
            w.Put(m.Revision);
            w.Put(m.Timestamp);
            w.Put(m.IsFightCommit);
        }

        public static bool TryReadDialogCommit(NetDataReader r, out NetBossDialogCommit result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != DialogCommitVersion) return false;
                var m = new NetBossDialogCommit
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    GraphName = r.GetString(),
                    RootName = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                m.CommitSource = r.GetString();
                m.Revision = r.GetInt();
                m.Timestamp = r.GetFloat();
                m.IsFightCommit = r.GetBool();
                result = m;
                return true;
            }
            catch { return false; }
        }

        public static void WriteBossState(NetDataWriter w, NetBossState s)
        {
            w.Put(BossStateVersion);
            w.Put(s.EncounterKey ?? "");
            w.Put(s.BossType ?? "");
            w.Put(s.ChapterName ?? "");
            w.Put(s.LevelIndex);
            w.Put(s.HasSeed);
            if (s.HasSeed) w.Put(s.Seed);
            w.Put(s.PhaseIndex);
            w.Put(s.FightStarted);
            w.Put(s.IntroFinished);
            w.Put(s.HasHealth);
            if (s.HasHealth) { w.Put(s.CurrentHealth); w.Put(s.MaxHealth); }
            w.Put(s.AliveAdds);
            w.Put(s.Revision);
            w.Put(s.Timestamp);
        }

        public static bool TryReadBossState(NetDataReader r, out NetBossState result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != BossStateVersion) return false;
                var s = new NetBossState
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (s.HasSeed) s.Seed = r.GetInt();
                s.PhaseIndex = r.GetInt();
                s.FightStarted = r.GetBool();
                s.IntroFinished = r.GetBool();
                s.HasHealth = r.GetBool();
                if (s.HasHealth) { s.CurrentHealth = r.GetFloat(); s.MaxHealth = r.GetFloat(); }
                s.AliveAdds = r.GetInt();
                s.Revision = r.GetInt();
                s.Timestamp = r.GetFloat();
                result = s;
                return true;
            }
            catch { return false; }
        }

        public static void WriteDynamicSpawn(NetDataWriter w, NetBossDynamicSpawn s)
        {
            w.Put(DynamicSpawnVersion);
            w.Put(s.EncounterKey ?? "");
            w.Put(s.OwnerBossType ?? "");
            w.Put(s.AddType ?? "");
            w.Put(s.AddUnitId ?? "");
            w.Put(s.SequenceIndex);
            w.Put(s.Position.x); w.Put(s.Position.y); w.Put(s.Position.z);
            w.Put(s.HostInstanceId);
            w.Put(s.ChapterName ?? "");
            w.Put(s.LevelIndex);
            w.Put(s.HasSeed);
            if (s.HasSeed) w.Put(s.Seed);
            w.Put(s.Revision);
            w.Put(s.Timestamp);
            w.Put(s.UnitIdValue);     // RT3
            w.Put(s.HostSpawnIndex);  // RT3
        }

        public static bool TryReadDynamicSpawn(NetDataReader r, out NetBossDynamicSpawn result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != DynamicSpawnVersion) return false;
                var s = new NetBossDynamicSpawn
                {
                    EncounterKey = r.GetString(),
                    OwnerBossType = r.GetString(),
                    AddType = r.GetString(),
                    AddUnitId = r.GetString(),
                    SequenceIndex = r.GetInt(),
                    Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                    HostInstanceId = r.GetInt(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (s.HasSeed) s.Seed = r.GetInt();
                s.Revision = r.GetInt();
                s.Timestamp = r.GetFloat();
                s.UnitIdValue = r.GetInt();     // RT3
                s.HostSpawnIndex = r.GetInt();  // RT3
                result = s;
                return true;
            }
            catch { return false; }
        }

        public static void WriteBossHit(NetDataWriter w, NetClientBossHitRequest h)
        {
            w.Put(BossHitVersion);
            w.Put(h.EncounterKey ?? "");
            w.Put(h.BossType ?? "");
            w.Put(h.RootName ?? "");
            w.Put(h.ChapterName ?? "");
            w.Put(h.LevelIndex);
            w.Put(h.HasSeed);
            if (h.HasSeed) w.Put(h.Seed);
            w.Put(h.TargetRole ?? "main");
            w.Put(h.Damage);
            w.Put(h.DamageTypeInt);
            w.Put(h.RequestSeq);
            w.Put(h.ClientPeerId ?? "");
            w.Put(h.SentAt);
        }

        public static bool TryReadBossHit(NetDataReader r, out NetClientBossHitRequest result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != BossHitVersion) return false;
                var h = new NetClientBossHitRequest
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    RootName = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (h.HasSeed) h.Seed = r.GetInt();
                h.TargetRole = r.GetString();
                h.Damage = r.GetFloat();
                h.DamageTypeInt = r.GetInt();
                h.RequestSeq = r.GetInt();
                h.ClientPeerId = r.GetString();
                h.SentAt = r.GetFloat();
                result = h;
                return true;
            }
            catch { return false; }
        }

        public static void WriteBossHitVisual(NetDataWriter w, NetHostBossHitVisual v)
        {
            w.Put(BossHitVisualVersion);
            w.Put(v.EncounterKey ?? "");
            w.Put(v.BossType ?? "");
            w.Put(v.TargetRole ?? "main");
            w.Put(v.TargetUnitId ?? "");
            w.Put(v.Seq);
        }

        public static bool TryReadBossHitVisual(NetDataReader r, out NetHostBossHitVisual result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != BossHitVisualVersion) return false;
                result = new NetHostBossHitVisual
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    TargetRole = r.GetString(),
                    TargetUnitId = r.GetString(),
                    Seq = r.GetInt(),
                };
                return true;
            }
            catch { return false; }
        }

        public static void WriteDiscreteEvent(NetDataWriter w, NetBossDiscreteEvent e)
        {
            w.Put(DiscreteEventVersion);
            w.Put(e.EncounterKey ?? "");
            w.Put(e.BossType ?? "");
            w.Put(e.EventName ?? "");
            w.Put(e.HasPos);
            if (e.HasPos) { w.Put(e.Position.x); w.Put(e.Position.y); w.Put(e.Position.z); }
            w.Put(e.ChapterName ?? "");
            w.Put(e.LevelIndex);
            w.Put(e.HasSeed);
            if (e.HasSeed) w.Put(e.Seed);
            w.Put(e.Seq);
        }

        public static bool TryReadDiscreteEvent(NetDataReader r, out NetBossDiscreteEvent result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != DiscreteEventVersion) return false;
                var e = new NetBossDiscreteEvent
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    EventName = r.GetString(),
                    HasPos = r.GetBool(),
                };
                if (e.HasPos) e.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                e.ChapterName = r.GetString();
                e.LevelIndex = r.GetInt();
                e.HasSeed = r.GetBool();
                if (e.HasSeed) e.Seed = r.GetInt();
                e.Seq = r.GetInt();
                result = e;
                return true;
            }
            catch { return false; }
        }

        public static void WriteLuciaEyeReport(NetDataWriter w, NetLuciaEyeReport m)
        {
            w.Put(LuciaEyeReportVersion);
            w.Put(m.EncounterKey ?? "");
            w.Put(m.BossType ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
            w.Put(m.Cycle);
            w.Put(m.LocalRemaining);
            w.Put(m.ReportSeq);
            w.Put(m.ClientPeerId ?? "");
            w.Put(m.SentAt);
        }

        public static bool TryReadLuciaEyeReport(NetDataReader r, out NetLuciaEyeReport result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != LuciaEyeReportVersion) return false;
                var m = new NetLuciaEyeReport
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                m.Cycle = r.GetInt();
                m.LocalRemaining = r.GetInt();
                m.ReportSeq = r.GetInt();
                m.ClientPeerId = r.GetString();
                m.SentAt = r.GetFloat();
                result = m;
                return true;
            }
            catch { return false; }
        }

        public static void WriteLuciaEyeState(NetDataWriter w, NetLuciaEyeState s)
        {
            w.Put(LuciaEyeStateVersion);
            w.Put(s.EncounterKey ?? "");
            w.Put(s.BossType ?? "");
            w.Put(s.ChapterName ?? "");
            w.Put(s.LevelIndex);
            w.Put(s.HasSeed);
            if (s.HasSeed) w.Put(s.Seed);
            w.Put(s.Cycle);
            w.Put(s.LivingEyes);
            w.Put(s.Revision);
            w.Put(s.Timestamp);
        }

        public static bool TryReadLuciaEyeState(NetDataReader r, out NetLuciaEyeState result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != LuciaEyeStateVersion) return false;
                var s = new NetLuciaEyeState
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (s.HasSeed) s.Seed = r.GetInt();
                s.Cycle = r.GetInt();
                s.LivingEyes = r.GetInt();
                s.Revision = r.GetInt();
                s.Timestamp = r.GetFloat();
                result = s;
                return true;
            }
            catch { return false; }
        }

        public static void WriteLuciaDeath(NetDataWriter w, NetLuciaDeath m)
        {
            w.Put(LuciaDeathVersion);
            w.Put(m.EncounterKey ?? "");
            w.Put(m.BossType ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
            w.Put(m.Revision);
            w.Put(m.Timestamp);
        }

        public static bool TryReadLuciaDeath(NetDataReader r, out NetLuciaDeath result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != LuciaDeathVersion) return false;
                var m = new NetLuciaDeath
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                m.Revision = r.GetInt();
                m.Timestamp = r.GetFloat();
                result = m;
                return true;
            }
            catch { return false; }
        }

        public static void WriteWitchPhase(NetDataWriter w, NetWitchPhase m)
        {
            w.Put(WitchPhaseVersion);
            w.Put(m.EncounterKey ?? "");
            w.Put(m.BossType ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
            w.Put(m.PhaseIndex);
            w.Put(m.PhaseRevision);
            w.Put(m.FightStarted);
            w.Put(m.Timestamp);
        }

        public static bool TryReadWitchPhase(NetDataReader r, out NetWitchPhase result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != WitchPhaseVersion) return false;
                var m = new NetWitchPhase
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                m.PhaseIndex = r.GetInt();
                m.PhaseRevision = r.GetInt();
                m.FightStarted = r.GetBool();
                m.Timestamp = r.GetFloat();
                result = m;
                return true;
            }
            catch { return false; }
        }

        public static void WriteWitchP2Manifest(NetDataWriter w, NetWitchP2Manifest m)
        {
            w.Put(WitchP2ManifestVersion);
            w.Put(m.EncounterKey ?? "");
            w.Put(m.BossType ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
            w.Put(m.PhaseRevision);
            w.Put(m.DomeCount);
            w.Put(m.RealDomeIndex);
            w.Put(m.Timestamp);
        }

        public static bool TryReadWitchP2Manifest(NetDataReader r, out NetWitchP2Manifest result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != WitchP2ManifestVersion) return false;
                var m = new NetWitchP2Manifest
                {
                    EncounterKey = r.GetString(),
                    BossType = r.GetString(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                m.PhaseRevision = r.GetInt();
                m.DomeCount = r.GetInt();
                m.RealDomeIndex = r.GetInt();
                m.Timestamp = r.GetFloat();
                result = m;
                return true;
            }
            catch { return false; }
        }

        public static void WriteWitchP2Result(NetDataWriter w, NetWitchP2Result m)
        {
            w.Put(WitchP2ResultVersion);
            w.Put(m.EncounterKey ?? "");
            w.Put(m.PhaseRevision);
            w.Put(m.DomeIndex);
            w.Put(m.Kind);
        }

        public static bool TryReadWitchP2Result(NetDataReader r, out NetWitchP2Result result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != WitchP2ResultVersion) return false;
                result = new NetWitchP2Result
                {
                    EncounterKey = r.GetString(),
                    PhaseRevision = r.GetInt(),
                    DomeIndex = r.GetInt(),
                    Kind = r.GetByte(),
                };
                return true;
            }
            catch { return false; }
        }
    }
}
