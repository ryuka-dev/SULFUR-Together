using System;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// SM-1: <c>BlackGuildSapper.InstantiateMine</c> (PerfectRandom.Sulfur.Gameplay, not referenced at compile time) is
    /// where a sapper lays a mine. A prefix + finalizer bracket it on the host so the mine's spawn is broadcast, and keep
    /// a linked client's copy of a sapper from laying one of its own (<see cref="SapperMineSync"/>).
    /// </summary>
    internal static class SapperMinePatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.BlackGuildSapper");
                var method = type != null ? AccessTools.DeclaredMethod(type, "InstantiateMine", Type.EmptyTypes) : null;
                if (method == null)
                {
                    Plugin.Log.Warn("[SapperMine] BlackGuildSapper.InstantiateMine not found — a sapper's mines are not synced");
                    return;
                }
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(SapperMinePatches), nameof(InstantiateMine_Pre)),
                    finalizer: new HarmonyMethod(typeof(SapperMinePatches), nameof(InstantiateMine_Exit)));
                Plugin.Log.Info("[SapperMine] Patched BlackGuildSapper.InstantiateMine (a sapper's mines reach every end).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SapperMine] patching failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static bool InstantiateMine_Pre(Component __instance) => SapperMineSync.OnInstantiateMinePre(__instance);

        private static Exception? InstantiateMine_Exit(Exception? __exception)
        {
            SapperMineSync.OnInstantiateMineExit();
            return __exception;
        }
    }
}
