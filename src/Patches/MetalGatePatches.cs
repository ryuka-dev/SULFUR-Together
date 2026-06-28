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

                // LD-2d grace: block a gate's close while a grace window is active near it (held open ~5 s so teammates
                // can still walk in); the host's CloseDoor closes it for real at t0+5 s.
                var close = AccessTools.DeclaredMethod(type, "Close", Type.EmptyTypes);
                if (close != null)
                    harmony.Patch(close, prefix: new HarmonyMethod(
                        typeof(MetalGatePatches).GetMethod(nameof(MetalGate_Close_Pre), BindingFlags.Static | BindingFlags.NonPublic)));

                Plugin.Log.Info("[GateSync] Patched MetalGate.Awake/Close/Open (combat-room gate sync).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GateSync] Apply failed: {ex.Message}"); }

            // Phase LD-1b (door SetActive sync) + LD-2a (arena lockdown membership feed) both hook PlayerTrigger.Trigger.
            try
            {
                if (!Plugin.Cfg.EnableTriggerDoorSync.Value && !Plugin.Cfg.EnableArenaLockdown.Value)
                { Plugin.Log.Info("[DoorSync] PlayerTrigger hook disabled by config."); return; }
                var pt = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.PlayerTrigger")
                      ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.PlayerTrigger");
                if (pt == null) { Plugin.Log.Warn("[DoorSync] PlayerTrigger type not found — trigger-door sync disabled."); return; }
                var trig = AccessTools.Method(pt, "Trigger", new[] { typeof(UnityEngine.GameObject) });
                if (trig == null) { Plugin.Log.Warn("[DoorSync] PlayerTrigger.Trigger(GameObject) not found (skipped)."); return; }
                harmony.Patch(trig,
                    prefix: new HarmonyMethod(
                        typeof(MetalGatePatches).GetMethod(nameof(PlayerTrigger_Trigger_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(
                        typeof(MetalGatePatches).GetMethod(nameof(PlayerTrigger_Trigger_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[DoorSync] Patched PlayerTrigger.Trigger (SetActive-door sync).");

                // LD-2e: attach a doorway-crossing sensor to seal triggers at Start (present before the first crossing,
                // so traversal parity = in/out is counted from the beginning).
                if (Plugin.Cfg.EnableArenaLockdown.Value)
                {
                    var start = AccessTools.DeclaredMethod(pt, "Start", Type.EmptyTypes);
                    if (start != null)
                        harmony.Patch(start, postfix: new HarmonyMethod(
                            typeof(MetalGatePatches).GetMethod(nameof(PlayerTrigger_Start_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                }
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

        // LD-2d: block the gate's close while a grace window is active near it (the gate stays open during grace). The
        // close postfix sees __runOriginal=false and so never captures/broadcasts — no extra sync needed.
        private static bool MetalGate_Close_Pre(object __instance)
        {
            try
            {
                if (__instance is UnityEngine.Component c && c != null
                    && ArenaLockdownManager.IsGateInLocalGrace(c.transform.position))
                    return false; // hold the gate open; host's CloseDoor closes it at t0+5 s
            }
            catch { }
            return true;
        }

        // LD-2d: start the local grace window BEFORE the trigger fires its events, so the same-frame MetalGate.Close()
        // is blocked. Membership is still reported in the postfix below (grace only defers the door).
        private static void PlayerTrigger_Trigger_Pre(object __instance)
        {
            ArenaLockdownManager.BeginLocalGraceIfSeal(__instance);
        }

        // LD-2e: attach the doorway-crossing sensor when a seal trigger starts (counts in/out parity from the first pass).
        private static void PlayerTrigger_Start_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            ArenaLockdownManager.AttachDoorwaySensorIfSeal(__instance);
        }

        private static void PlayerTrigger_Trigger_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            TriggerDoorSyncManager.CaptureLocalTrigger(__instance);
            ArenaLockdownManager.OnLocalTriggerFired(__instance); // LD-2a: arena lockdown membership feed
        }
    }
}
