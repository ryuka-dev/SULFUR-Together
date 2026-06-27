using System;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase LD-1 generic combat-room gate (MetalGate) sync — capture hooks.
    /// <para><c>MetalGate</c> lives in the Gameplay assembly (not referenced), so it is resolved by name and patched
    /// via reflection. <c>Awake</c> postfix registers the gate (+ its static world position). <c>Close</c>/<c>Open</c>
    /// postfixes are the single chokepoint for every gate state change (PlayerTrigger room-seal, MetalGateTrigger,
    /// AllDeadTrigger open, startClosed init, witch car-chase) — each broadcasts the new state so other peers mirror it.
    /// A mirrored change sets <see cref="GateSyncManager.IsApplyingMirror"/> so it never re-broadcasts.</para>
    /// </summary>
    internal static class MetalGatePatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                if (!Plugin.Cfg.EnableGateSync.Value) { Plugin.Log.Info("[GateSync] disabled by config."); return; }

                var type = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.Mechanisms.MetalGate.MetalGate");
                if (type == null)
                {
                    Plugin.Log.Warn("[GateSync] MetalGate type not found — gate sync disabled.");
                    return;
                }

                Patch(harmony, type, "Awake", nameof(MetalGate_Awake_Post));
                Patch(harmony, type, "Close", nameof(MetalGate_Close_Post));
                Patch(harmony, type, "Open",  nameof(MetalGate_Open_Post));

                Plugin.Log.Info("[GateSync] Patched MetalGate.Awake/Close/Open (combat-room gate sync).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GateSync] Apply failed: {ex.Message}"); }

            // Phase LD-1b: doors activated via GameObject.SetActive from a PlayerTrigger (Lucia etc.) — not a MetalGate.
            try
            {
                if (!Plugin.Cfg.EnableTriggerDoorSync.Value) { Plugin.Log.Info("[DoorSync] disabled by config."); return; }
                var pt = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.PlayerTrigger")
                      ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.PlayerTrigger");
                if (pt == null) { Plugin.Log.Warn("[DoorSync] PlayerTrigger type not found — trigger-door sync disabled."); return; }
                var trig = AccessTools.Method(pt, "Trigger", new[] { typeof(UnityEngine.GameObject) });
                if (trig == null) { Plugin.Log.Warn("[DoorSync] PlayerTrigger.Trigger(GameObject) not found (skipped)."); return; }
                harmony.Patch(trig, postfix: new HarmonyMethod(
                    typeof(MetalGatePatches).GetMethod(nameof(PlayerTrigger_Trigger_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[DoorSync] Patched PlayerTrigger.Trigger (SetActive-door sync).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[DoorSync] Apply failed: {ex.Message}"); }
        }

        private static void Patch(Harmony harmony, Type type, string method, string postfixName)
        {
            var mi = AccessTools.DeclaredMethod(type, method, Type.EmptyTypes);
            if (mi == null) { Plugin.Log.Warn($"[GateSync] MetalGate.{method} not found (skipped)."); return; }
            harmony.Patch(mi, postfix: new HarmonyMethod(
                typeof(MetalGatePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void MetalGate_Awake_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            GateSyncManager.Register(__instance);
        }

        private static void MetalGate_Close_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            GateSyncManager.CaptureLocalGate(__instance, closed: true);
        }

        private static void MetalGate_Open_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            GateSyncManager.CaptureLocalGate(__instance, closed: false);
        }

        private static void PlayerTrigger_Trigger_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            TriggerDoorSyncManager.CaptureLocalTrigger(__instance);
        }
    }
}
