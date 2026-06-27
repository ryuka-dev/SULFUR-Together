using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase RT3-Cousin-arms: turn the Cousin arm's single-target throw into an all-players AoE, and make the client
    /// mirror it visually (de-fanged).
    ///
    /// Vanilla <c>CousinArm.ThrowProjectile()</c> (fired from a throw ANIMATION EVENT) lobs ONE physical mud ball at
    /// <c>npc.AiAgent.target</c> — the single nearest player (GetClosestPlayer). The co-op design (user spec) is a
    /// group attack: the arm throws one physical ball at EVERY player. Because the balls are physical (not homing),
    /// players standing in a line all get hit by the front-most one.
    ///
    /// We patch <c>ThrowProjectile</c>: instead of the one native throw we loop over <c>GameManager.Players</c> and run
    /// the REAL throw once per player (re-invoking the method under a reentrancy flag, so the ball uses the arm's own
    /// barrel/arc/visual). Because the call originates from the animation event, every ball stays aligned with the
    /// throw animation. On the HOST each ball deals real damage (host-authoritative; a ball aimed at a remote player
    /// hits that player's target-proxy and routes to the client). On the CLIENT every ball is de-fanged (damage 0) —
    /// purely the visual; the client's GameManager.Players includes the local player plus the host's ghost proxy.
    /// </summary>
    internal static class CousinArmPatches
    {
        private static Type? _cousinArmType;
        private static FieldInfo? _npcField;       // CousinArm.npc
        private static FieldInfo? _damageField;    // CousinArm.damage
        private static MethodInfo? _throwMethod;   // CousinArm.ThrowProjectile
        private static FieldInfo? _aiAgentTargetField;
        private static FieldInfo? _playerUnitField; // Player.playerUnit
        private static Type? _gmType;
        private static PropertyInfo? _gmInstance;
        private static PropertyInfo? _gmPlayers;
        private static PropertyInfo? _gmPlayerUnit;

        private static bool _inThrowLoop;
        private static float _lastLogAt;

        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Apply(Harmony harmony)
        {
            if (!Plugin.Cfg.EnableCousinArmSync.Value)
            {
                Plugin.Log.Info("[ArmThrow] CousinArm AoE/visual patch disabled by config.");
                return;
            }

            _cousinArmType = FindGameType("PerfectRandom.Sulfur.Gameplay.CousinArm")
                          ?? FindGameType("PerfectRandom.Sulfur.Core.CousinArm")
                          ?? FindGameType("CousinArm");
            if (_cousinArmType == null) { Plugin.Log.Info("[ArmThrow] CousinArm type not found."); return; }

            _npcField = _cousinArmType.GetField("npc", BF);
            _damageField = _cousinArmType.GetField("damage", BF);
            _throwMethod = _cousinArmType.GetMethod("ThrowProjectile", BF, null, Type.EmptyTypes, null);

            _gmType = FindGameType("PerfectRandom.Sulfur.Core.GameManager") ?? FindGameType("GameManager");
            _gmInstance = _gmType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            _gmPlayers = _gmType?.GetProperty("Players", BindingFlags.Instance | BindingFlags.Public);
            _gmPlayerUnit = _gmType?.GetProperty("PlayerUnit", BindingFlags.Instance | BindingFlags.Public);

            if (_throwMethod == null) { Plugin.Log.Info("[ArmThrow] CousinArm.ThrowProjectile not found."); return; }

            var pre = new HarmonyMethod(typeof(CousinArmPatches).GetMethod(nameof(ThrowProjectile_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            try { harmony.Patch(_throwMethod, prefix: pre); Plugin.Log.Info($"[ArmThrow] patched CousinArm.ThrowProjectile (npc={_npcField != null} damage={_damageField != null} players={_gmPlayers != null})"); }
            catch (Exception ex) { Plugin.Log.Error($"[ArmThrow] patch CousinArm.ThrowProjectile failed: {ex.Message}"); }
        }

        // Runs from the arm's throw animation event. We replace the single native throw with one throw per player.
        private static bool ThrowProjectile_Pre(object __instance)
        {
            if (_inThrowLoop) return true; // inner re-invoke: run the real throw at the current target
            try
            {
                if (__instance == null || !Plugin.Cfg.EnableCousinArmSync.Value) return true;
                bool client = NetConfig.GetMode() == NetMode.Client;

                // Client: de-fang every throw this arm makes (visual only; damage stays host-authoritative).
                if (client) { try { _damageField?.SetValue(__instance, 0f); } catch { } }

                object? npc = _npcField?.GetValue(__instance);
                object? aiAgent = npc == null ? null : (GetMember(npc, "AiAgent") ?? GetMember(npc, "aiAgent"));
                if (aiAgent == null) return true; // can't drive targeting; let the original throw once
                if (_aiAgentTargetField == null)
                    _aiAgentTargetField = aiAgent.GetType().GetField("target", BF);
                if (_aiAgentTargetField == null) return true;

                var units = GatherPlayerUnits();
                if (units.Count == 0)
                {
                    // No player list resolved — fall back to the native single throw (ensure it has a target).
                    object? cur = _aiAgentTargetField.GetValue(aiAgent);
                    if (cur == null || (cur is UnityEngine.Object uo && uo == null))
                    {
                        object? lp = ResolveLocalPlayerUnit();
                        if (lp != null) { try { _aiAgentTargetField.SetValue(aiAgent, lp); } catch { } }
                    }
                    return true;
                }

                object? prevTarget = _aiAgentTargetField.GetValue(aiAgent);
                int thrown = 0;
                _inThrowLoop = true;
                try
                {
                    foreach (var u in units)
                    {
                        if (u == null) continue;
                        try { _aiAgentTargetField.SetValue(aiAgent, u); }
                        catch { continue; }
                        try { _throwMethod!.Invoke(__instance, null); thrown++; }
                        catch (Exception ex) { Plugin.Log.Warn($"[ArmThrow] per-player throw failed: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
                finally
                {
                    _inThrowLoop = false;
                    try { _aiAgentTargetField.SetValue(aiAgent, prevTarget); } catch { }
                }

                if (Plugin.Cfg.LogBossDynamicSpawn.Value)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _lastLogAt > 0.5f) { _lastLogAt = now; Plugin.Log.Info($"[ArmThrow] {(client ? "client visual" : "host")} AoE throw players={units.Count} thrown={thrown}"); }
                }
                return false; // we issued all throws; skip the native single throw
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ArmThrow] ThrowProjectile_Pre failed: {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        // Every player's damageable Unit (GameManager.Players[i].playerUnit). Host: local + ghost proxies of clients;
        // Client: local + the host's ghost proxy.
        private static readonly List<object> _unitScratch = new List<object>(4);
        private static List<object> GatherPlayerUnits()
        {
            _unitScratch.Clear();
            try
            {
                var gm = _gmInstance?.GetValue(null);
                if (gm == null) return _unitScratch;
                if (!(_gmPlayers?.GetValue(gm) is IEnumerable players)) return _unitScratch;
                foreach (var p in players)
                {
                    if (p == null) continue;
                    if (_playerUnitField == null) _playerUnitField = p.GetType().GetField("playerUnit", BF);
                    object? u = _playerUnitField?.GetValue(p);
                    if (u == null || (u is UnityEngine.Object uo && uo == null)) continue;
                    _unitScratch.Add(u);
                }
            }
            catch { }
            return _unitScratch;
        }

        private static object? ResolveLocalPlayerUnit()
        {
            try { var gm = _gmInstance?.GetValue(null); return gm == null ? null : _gmPlayerUnit?.GetValue(gm); }
            catch { return null; }
        }

        private static object? GetMember(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                var p = t.GetProperty(name, BF);
                if (p != null) return p.GetValue(obj);
                var f = t.GetField(name, BF);
                return f?.GetValue(obj);
            }
            catch { return null; }
        }

        private static Type? FindGameType(string fullName)
        {
            try { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(fullName); if (t != null) return t; } } catch { }
            return null;
        }
    }
}
