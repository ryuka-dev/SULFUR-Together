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
            WeaponFirePatches.Apply(harmony);
            PauseControlPatches.Apply(harmony);
            Plugin.Log.Info("[PatchBootstrap] Done.");
        }
    }
}
