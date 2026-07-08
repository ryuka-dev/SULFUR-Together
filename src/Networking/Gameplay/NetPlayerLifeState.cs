using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal enum NetPlayerLifeStateKind : byte
    {
        Alive = 0,
        Downed = 1,
        ReviveRequest = 2,
        ReviveAccepted = 3,
        NativeDeathCommit = 4,
        HostDamageRequest = 5,
        // RS-1: Client→Host, "I actually took N post-mitigation damage." Only the Client can compute this number for
        // itself (it applies the Host-forwarded HostDamageRequest via the real ReceiveDamage locally); the Host cannot
        // observe it any other way. DamageAmount carries the real before/after HP delta. Reuses this existing
        // channel/DTO rather than adding a new NetMessageType — same identity fields, one more Kind.
        DamageTakenReport = 6,

        // DR-1: Client→Host, "my player started/stopped holding the revive key near a downed teammate."
        // SourcePeerId=rescuer, TargetPeerId=downed peer. Replaces the old client-local hold timer — the Host now
        // owns the authoritative rescue clock so both ends see the identical progress value (never independently
        // timed locally).
        RescueHoldStart = 7,
        RescueHoldStop = 8,

        // DR-1: Host→All, authoritative rescue progress tick while a rescue is active. SourcePeerId=rescuer,
        // TargetPeerId=downed, Progress=0..1. Sent immediately on start, then periodically while held. Completion
        // (Progress reaching 1) is reported via the existing ReviveAccepted kind, not this one.
        RescueProgress = 9,

        // DR-1: Host→All, one-shot — the Host invalidated the active rescue (rescuer released the key / left
        // range / died, or the target stopped being Downed for some other reason) before it completed.
        RescueCancelled = 10,
    }

    internal sealed class NetPlayerLifeState
    {
        public string EventId { get; set; } = "";
        public string SourcePeerId { get; set; } = "";
        public string TargetPeerId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string ChapterName { get; set; } = "<unknown>";
        public int LevelIndex { get; set; } = -1;
        public bool HasLevelSeed { get; set; }
        public int LevelSeed { get; set; }
        public int Sequence { get; set; }
        public NetPlayerLifeStateKind Kind { get; set; } = NetPlayerLifeStateKind.Alive;
        public bool HasPosition { get; set; }
        public Vector3 Position { get; set; }
        public float SentAt { get; set; }
        public string Reason { get; set; } = "";
        public float DamageAmount { get; set; }
        // PerfectRandom.Sulfur.Core.Stats.DamageTypes value (0 = None). When 0 the client substitutes the configured
        // physical default — Unit.ReceiveDamage rejects None outright ("Missing DamageType!" + returns false).
        public int DamageType { get; set; }

        // DR-1: authoritative rescue progress, 0..1. Only meaningful on RescueProgress (Host→All); zero/unused on
        // every other Kind.
        public float Progress { get; set; }

        public bool HasScene => !string.IsNullOrWhiteSpace(ChapterName) && ChapterName != "<unknown>" && LevelIndex >= 0;
        public string SceneKey => NetSceneName.SceneCompareKey(ChapterName, LevelIndex);
        public string SeedText => HasLevelSeed ? LevelSeed.ToString() : "?";

        public bool MatchesScene(NetRunState state)
        {
            if (!HasScene || !state.HasLevel) return false;
            if (!NetSceneName.SameScene(ChapterName, LevelIndex, state.ChapterName, state.LevelIndex)) return false;
            if (HasLevelSeed && state.HasLevelSeed && LevelSeed != state.LevelSeed) return false;
            return true;
        }

        public string ToCompactString()
        {
            string src = string.IsNullOrWhiteSpace(SourcePeerId) ? "?" : SourcePeerId;
            string dst = string.IsNullOrWhiteSpace(TargetPeerId) ? "" : $",target={TargetPeerId}";
            string pos = HasPosition ? $",pos=({Position.x:F2},{Position.y:F2},{Position.z:F2})" : ",pos=?";
            string reason = string.IsNullOrWhiteSpace(Reason) ? "" : $",reason={Reason}";
            string damage = DamageAmount > 0f ? $",damage={DamageAmount:F1}" : "";
            return $"kind={Kind},src={src}{dst},scene={SceneKey}#seed={SeedText},seq={Sequence}{pos}{damage}{reason}";
        }
    }
}
