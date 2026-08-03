using HarmonyLib;

namespace SULFURTogether.Patches
{
    public static class PatchBootstrap
    {
        public static void ApplyAll(Harmony harmony)
        {
            Plugin.Log.Info("[PatchBootstrap] Applying reverse probe patches...");
            ReverseProbePatches.Apply(harmony);
            AwaitStartLevelPatches.Apply(harmony);
            SeamlessStartLevelPatches.Apply(harmony);   // NP-3 — must follow AWAIT-3 (shares its MoveNext seam)
            RangedShootProbePatches.Apply(harmony);     // FH-6 — split the TriggerShoot stall (diagnostic)
            GhostSpawnPatches.Apply(harmony);
            CryptChallengePatches.Apply(harmony);
            CardinalPatches.Apply(harmony);             // BGC — Black Guild Cardinal alcove fight
            LevelGenTracePatches.Apply(harmony);
            BossEncounterPatches.Apply(harmony);
            BossSpawnPatches.Apply(harmony);
            CousinArmPatches.Apply(harmony);
            DeathSpawnSyncPatches.Apply(harmony);
            WeaponFirePatches.Apply(harmony);
            BreakablePatches.Apply(harmony);
            ThrowablePatches.Apply(harmony);
            TriggerSpawnPatches.Apply(harmony);
            MetalGatePatches.Apply(harmony);
            DoorBlockerPatches.Apply(harmony);
            OpenableDoorPatches.Apply(harmony);
            PickupPatches.Apply(harmony);
            LootRollSuppressionPatches.Apply(harmony);
            ChestPatches.Apply(harmony);
            LootablePatches.Apply(harmony);
            TargetDummyPatches.Apply(harmony);
            PauseControlPatches.Apply(harmony);
            HazardProbePatches.Apply(harmony);
            EndlessModeProbePatches.Apply(harmony);
            EndlessSyncPatches.Apply(harmony);
            EndlessBossBarFixPatches.Apply(harmony);
            GhostPlayerPatches.Apply(harmony);
            UnitStatusPatches.Apply(harmony);
            Plugin.Log.Info("[PatchBootstrap] Done.");
        }
    }
}
