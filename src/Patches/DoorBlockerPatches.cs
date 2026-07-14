using System;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase DB-1 inter-chunk door (<c>DoorBlocker</c>) sync — capture + registry hooks.
    /// <para><c>ActivateDoorBlocker</c> postfix: level gen calls this on every door it places, and it carries the
    /// authoritative <c>isAnClosingDoor</c> flag — the real event source for "does this door start closed", which is
    /// what scopes the sync (see <see cref="DoorBlockerSyncManager.Register"/>). We bind only the parameters we read,
    /// so the <c>Connector</c> parameter 0.18 added to this method is irrelevant to us.</para>
    /// <para><c>OnFinishedHolding</c> postfix: the door just finished opening locally. For a registered (starts-closed)
    /// door this can only be a player completing a hold, so broadcast it; other peers mirror the real open. A mirrored
    /// open sets <see cref="DoorBlockerSyncManager.IsApplyingMirror"/> so it never re-broadcasts.</para>
    /// </summary>
    internal static class DoorBlockerPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                // Bind by name only: DoorBlocker declares one overload of each, and pinning a parameter table is how
                // a hook silently dies when the game adds a parameter (0.18 added Connector to ActivateDoorBlocker).
                var activate = AccessTools.DeclaredMethod(typeof(DoorBlocker), "ActivateDoorBlocker");
                if (activate != null)
                    harmony.Patch(activate, postfix: new HarmonyMethod(
                        typeof(DoorBlockerPatches).GetMethod(nameof(ActivateDoorBlocker_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ChunkDoor] DoorBlocker.ActivateDoorBlocker not found — door registry disabled.");

                var finished = AccessTools.DeclaredMethod(typeof(DoorBlocker), "OnFinishedHolding");
                if (finished != null)
                    harmony.Patch(finished, postfix: new HarmonyMethod(
                        typeof(DoorBlockerPatches).GetMethod(nameof(OnFinishedHolding_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ChunkDoor] DoorBlocker.OnFinishedHolding not found — door sync disabled.");

                Plugin.Log.Info($"[ChunkDoor] Patched DoorBlocker.ActivateDoorBlocker({activate != null})/OnFinishedHolding({finished != null}) (inter-chunk door sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[ChunkDoor] Apply failed: {ex.Message}");
            }
        }

        private static void ActivateDoorBlocker_Post(DoorBlocker __instance, bool isAnClosingDoor)
        {
            if (!Plugin.Cfg.EnableDoorBlockerSync.Value) return;
            DoorBlockerSyncManager.Register(__instance, isAnClosingDoor);
        }

        private static void OnFinishedHolding_Post(DoorBlocker __instance)
        {
            DoorBlockerSyncManager.CaptureLocalOpen(__instance);
        }
    }
}
