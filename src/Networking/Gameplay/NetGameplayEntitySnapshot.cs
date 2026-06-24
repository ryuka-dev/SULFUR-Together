using System;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Local-only structured snapshot for Phase 4 gameplay probes.
    /// Keeps structured identity fields plus a weak runtime object reference for opt-in experiments.
    /// </summary>
    internal sealed class NetGameplayEntitySnapshot
    {
        public NetGameplayEntityId EntityId { get; set; } = NetGameplayEntityId.FromObject(null);
        public string Category { get; set; } = "Unit";
        public string ActorName { get; set; } = "<unknown>";
        // Phase 4.4.0-O3: entity sync category — 0=Unknown,1=CombatEnemy,2=Trader,3=InteractiveNpc,4=Ghost,5=EventNpc,6=Ambient,7=Hazard
        public int SyncCategory { get; set; }
        public string Source { get; set; } = "";
        public int SpawnIndex { get; set; }
        public bool HasPosition { get; set; }
        public Vector3 Position { get; set; }
        // Phase 5.3-G: the FIRST position observed at spawn. Never updated afterwards, so it stays
        // stable for the generation-hash signature (current Position drifts as the unit moves).
        public bool HasInitialPosition { get; set; }
        public Vector3 InitialPosition { get; set; }
        public string SceneKey { get; set; } = "<unknown>:-1";
        public bool HasLevelSeed { get; set; }
        public int LevelSeed { get; set; }
        public string GameState { get; set; } = "<unknown>";
        public float FirstSeenAt { get; set; }
        public float LastSeenAt { get; set; }
        public int DamageCount { get; set; }
        public bool IsDead { get; set; }
        public bool PendingStableContextLog { get; set; }
        private WeakReference<object>? RuntimeObjectRef { get; set; }

        public string PositionText => HasPosition
            ? $"({Position.x:F2},{Position.y:F2},{Position.z:F2})"
            : "(?)";

        public string SeedText => HasLevelSeed ? LevelSeed.ToString() : "?";

        public void SetRuntimeObject(object? runtimeObject)
        {
            if (runtimeObject == null) return;
            RuntimeObjectRef = new WeakReference<object>(runtimeObject);
        }

        public bool TryGetRuntimeObject(out object? runtimeObject)
        {
            runtimeObject = null;
            if (RuntimeObjectRef == null) return false;
            if (!RuntimeObjectRef.TryGetTarget(out var target) || target == null) return false;
            runtimeObject = target;
            return true;
        }

        public string FormatSpawnLine(string phase)
        {
            return $"[GameplayProbe] Spawn{phase} idx={SpawnIndex} candidate={EntityId.CandidateKey} {EntityId.FormatCompact()} category={Category} actor={ActorName} pos={PositionText} scene={SceneKey} seed={SeedText} state={GameState} source={Source}";
        }

        public string FormatDeathLine(string source, float lifetime)
        {
            return $"[GameplayProbe] Death idx={SpawnIndex} candidate={EntityId.CandidateKey} category={Category} actor={ActorName} pos={PositionText} scene={SceneKey} seed={SeedText} damageCount={DamageCount} lifetime={lifetime:F1}s source={source}";
        }

        public string FormatDamageLine(string source, float damage, object? damageType)
        {
            return $"[GameplayProbe] Damage idx={SpawnIndex} candidate={EntityId.CandidateKey} category={Category} actor={ActorName} dmg={damage:F2} type={damageType} count={DamageCount} scene={SceneKey} seed={SeedText} source={source}";
        }
    }
}
