using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using SULFURTogether.Networking.Gameplay;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// ISO-1: a room isolation only applies on an end whose own player is in that room.
    /// <para>Several fights isolate the room they take place in — <c>DistanceLODManager.isolatedLOD</c> is set by
    /// <c>CousinHelper.StartFight</c>, <c>LuciaBossFightTrigger.TriggerFight</c>, <c>TerrorbaumBossFightHelper.TriggerFight</c>,
    /// <c>CardinalFightHelper.StartFight</c> and <c>CryptChallengeManager.StartChallenge</c> — and
    /// <c>DistanceLODManager.LateUpdate</c> then switches every other room's LOD off: renderers and colliders. In single
    /// player the player is always inside that room when it happens. In co-op the fight start reaches every end
    /// (host-authoritative), so a player still outside the room saw a black screen with no level around them (round 598,
    /// Cousin: the client at the level start, <c>isolatedLOD = 5-CaveCousinNew</c> on both ends).</para>
    /// <para>LateUpdate is the field's only reader, so for the duration of that call the isolation is withheld when the
    /// local player is not in the isolated room (the LOD sits on the room's own GameObject) — by <c>currentRoom</c>, or,
    /// when that still names the room left behind by a teleport, by the game's own <c>Room.FindClosestRoom</c>; that end
    /// runs its ordinary distance/room LOD instead. Once the player walks or is teleported in, the next LateUpdate applies
    /// the isolation as vanilla does. The field itself is never changed for longer than the call, so the fight's own
    /// clearing (<c>isolatedLOD = null</c> on its end) and every other reader see the real value. Outside a session, and
    /// when the room cannot be determined, vanilla runs untouched.</para>
    /// </summary>
    internal static class RoomIsolationPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var lateUpdate = AccessTools.Method(typeof(DistanceLODManager), "LateUpdate", Type.EmptyTypes);
                var field = AccessTools.Field(typeof(DistanceLODManager), "isolatedLOD");
                if (lateUpdate == null || field == null)
                {
                    Plugin.Log.Warn("[RoomIsolation] DistanceLODManager.LateUpdate/isolatedLOD not found — room isolation scoping not supported on this game build; "
                                    + "a player outside an isolated boss room may see a black level");
                    return;
                }
                harmony.Patch(lateUpdate,
                    prefix: new HarmonyMethod(typeof(RoomIsolationPatches).GetMethod(nameof(LateUpdate_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(RoomIsolationPatches).GetMethod(nameof(LateUpdate_Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[RoomIsolation] patched DistanceLODManager.LateUpdate (isolation only for a player inside the isolated room)");
            }
            catch (Exception ex) { Plugin.Log.Error($"[RoomIsolation] patch failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static RoomLODBase? _lastLogged;

        private static void LateUpdate_Pre(DistanceLODManager __instance, out RoomLODBase? __state)
        {
            __state = null;
            try
            {
                RoomLODBase? isolated = __instance.isolatedLOD;
                if (isolated == null || !NetGameplaySyncBridge.IsSessionActive) return;
                var gm = GameManager.Instance;
                var player = gm != null ? gm.PlayerUnit : null;
                Room? current = player != null ? player.currentRoom : null;
                Room? isolatedRoom = isolated.GetComponent<Room>();
                if (player == null || current == null || isolatedRoom == null) return;   // cannot tell: vanilla
                // currentRoom follows the floor last stood on, so right after a teleport into the room (the arena entry
                // teleport) it still names the old room until the player moves. The game's own position lookup (a short
                // ray down onto the room geometry, falling back to currentRoom) settles it on the spot. Only reached
                // while the isolation is set and currentRoom disagrees with it.
                bool inside = ReferenceEquals(current, isolatedRoom)
                              || ReferenceEquals(Room.FindClosestRoom(player.transform.position + Vector3.up * 0.5f, current), isolatedRoom);
                if (inside)
                {
                    if (ReferenceEquals(_lastLogged, isolated)) { _lastLogged = null; LogState(isolated, applied: true); }
                    return;
                }
                __state = isolated;
                __instance.isolatedLOD = null;
                if (!ReferenceEquals(_lastLogged, isolated)) { _lastLogged = isolated; LogState(isolated, applied: false); }
            }
            catch { __state = null; }
        }

        // A finalizer runs however LateUpdate ends (its early returns, or an exception), so the field is always restored.
        private static void LateUpdate_Finalizer(DistanceLODManager __instance, RoomLODBase? __state)
        {
            if (__state != null) __instance.isolatedLOD = __state;
        }

        private static void LogState(RoomLODBase isolated, bool applied)
        {
            if (applied)
                Plugin.Log.Info($"[RoomIsolation] local player entered isolated room {isolated.name} — isolation applies");
            else
                Plugin.Log.Info($"[RoomIsolation] room {isolated.name} is isolated but the local player is outside it — isolation withheld on this end");
        }
    }
}
