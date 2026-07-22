using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase KD (crypt sync) <c>OpenableDoor</c> open sync — capture hook.
    /// <para><c>OpenableDoor.Open()</c> (private) is the single funnel that opens the door — however it was triggered
    /// (a <c>KeyStation</c> insert, an item check, or a scripted event). Its postfix is the one place every open passes
    /// through, so it is the capture chokepoint. A mirrored open sets
    /// <see cref="OpenableDoorSyncManager.IsApplyingMirror"/> so it never re-broadcasts.</para>
    /// </summary>
    internal static class OpenableDoorPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                if (!Plugin.Cfg.EnableOpenableDoorSync.Value) { Plugin.Log.Info("[KeyDoor] disabled by config."); return; }

                var open = AccessTools.DeclaredMethod(typeof(OpenableDoor), "Open", Type.EmptyTypes);
                if (open == null)
                {
                    Plugin.Log.Warn("[KeyDoor] OpenableDoor.Open() not found — key-door sync disabled.");
                    return;
                }

                harmony.Patch(open, postfix: new HarmonyMethod(
                    typeof(OpenableDoorPatches).GetMethod(nameof(Open_Post), BindingFlags.Static | BindingFlags.NonPublic)));

                Plugin.Log.Info("[KeyDoor] Patched OpenableDoor.Open (locked/one-way door open sync).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[KeyDoor] Apply failed: {ex.Message}"); }
        }

        private static void Open_Post(OpenableDoor __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            OpenableDoorSyncManager.CaptureLocalOpen(__instance);
        }
    }
}
