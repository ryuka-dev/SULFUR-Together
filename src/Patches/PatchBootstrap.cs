using HarmonyLib;

namespace SULFURTogether.Patches
{
    public static class PatchBootstrap
    {
        public static void ApplyAll(Harmony harmony)
        {
            Plugin.Log.Info("[PatchBootstrap] Applying reverse probe patches...");
            ReverseProbePatches.Apply(harmony);
            LevelGenTracePatches.Apply(harmony);
            BossEncounterPatches.Apply(harmony);
            BossSpawnPatches.Apply(harmony);
            CousinArmPatches.Apply(harmony);
            DeathSpawnSyncPatches.Apply(harmony);
            WeaponFirePatches.Apply(harmony);
            BreakablePatches.Apply(harmony);
            MetalGatePatches.Apply(harmony);
            PickupPatches.Apply(harmony);
            PauseControlPatches.Apply(harmony);
            HazardProbePatches.Apply(harmony);
            Plugin.Log.Info("[PatchBootstrap] Done.");
        }
    }
}
