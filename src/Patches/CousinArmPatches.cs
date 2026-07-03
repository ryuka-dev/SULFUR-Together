using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase RT3-Cousin-arms: turn the Cousin arm's single-target throw into an all-players AoE, and make the client
    /// mirror it visually (de-fanged).
    ///
    /// Vanilla <c>CousinArm.ThrowProjectile()</c> (fired from a throw ANIMATION EVENT) lobs ONE physical mud ball at
    /// <c>npc.AiAgent.target</c> — the single nearest player. The co-op design (user spec) is a group attack: one
    /// physical ball at EVERY player; because the balls are physical (not homing), players in a line are all hit by the
    /// front-most one.
    ///
    /// We patch <c>ThrowProjectile</c> and loop over the players:
    ///  - HOST: re-invoke the REAL throw once per <c>GameManager.Players</c> entry (host + ghost proxies of clients) so
    ///    each ball deals real, host-authoritative damage (a ball aimed at a remote player hits its target-proxy and
    ///    routes to the client). Reentrancy flag lets us reuse the native method per target.
    ///  - CLIENT: <c>GameManager.Players</c> only has the local player (ghosts are host-only). We re-invoke the real
    ///    throw at the local player (de-fanged) AND, because there is no Unit for remote players client-side, we spawn a
    ///    VISUAL-ONLY ball (the arm's own <c>visualProjectile</c>, damage 0) toward each remote player's proxy position,
    ///    so a client sees the boss attacking everyone — not just itself. Damage stays host-authoritative.
    ///
    /// Because the call originates from the animation event, every ball stays aligned with the throw animation.
    /// </summary>
    internal static class CousinArmPatches
    {
        private static Type? _cousinArmType;
        private static FieldInfo? _npcField;        // CousinArm.npc
        private static FieldInfo? _damageField;     // CousinArm.damage
        private static FieldInfo? _barrelField;     // CousinArm.barrelTransform
        private static FieldInfo? _heightCurveField;// CousinArm.throwHeightCurve
        private static FieldInfo? _forceCurveField; // CousinArm.throwForceCurve
        private static FieldInfo? _visualField;     // CousinArm.visualProjectile
        private static FieldInfo? _prefabOnHitField;// CousinArm.prefabOnHit
        private static MethodInfo? _throwMethod;    // CousinArm.ThrowProjectile
        private static FieldInfo? _aiAgentTargetField;
        private static FieldInfo? _playerUnitField; // Player.playerUnit
        private static Type? _gmType;
        private static PropertyInfo? _gmInstance;
        private static PropertyInfo? _gmPlayers;

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
            _barrelField = _cousinArmType.GetField("barrelTransform", BF);
            _heightCurveField = _cousinArmType.GetField("throwHeightCurve", BF);
            _forceCurveField = _cousinArmType.GetField("throwForceCurve", BF);
            _visualField = _cousinArmType.GetField("visualProjectile", BF);
            _prefabOnHitField = _cousinArmType.GetField("prefabOnHit", BF);
            _throwMethod = _cousinArmType.GetMethod("ThrowProjectile", BF, null, Type.EmptyTypes, null);

            _gmType = FindGameType("PerfectRandom.Sulfur.Core.GameManager") ?? FindGameType("GameManager");
            _gmInstance = _gmType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            _gmPlayers = _gmType?.GetProperty("Players", BindingFlags.Instance | BindingFlags.Public);

            if (_throwMethod == null) { Plugin.Log.Info("[ArmThrow] CousinArm.ThrowProjectile not found."); return; }

            var pre = new HarmonyMethod(typeof(CousinArmPatches).GetMethod(nameof(ThrowProjectile_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            try { harmony.Patch(_throwMethod, prefix: pre); Plugin.Log.Info($"[ArmThrow] patched CousinArm.ThrowProjectile (npc={_npcField != null} barrel={_barrelField != null} visual={_visualField != null} players={_gmPlayers != null})"); }
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

                // Client: de-fang every real throw this arm makes (visual only; damage stays host-authoritative).
                if (client) { try { _damageField?.SetValue(__instance, 0f); } catch { } }

                object? npc = _npcField?.GetValue(__instance);
                object? aiAgent = npc == null ? null : (GetMember(npc, "AiAgent") ?? GetMember(npc, "aiAgent"));
                if (aiAgent == null) return true; // can't drive targeting; let the original throw once
                if (_aiAgentTargetField == null)
                    _aiAgentTargetField = aiAgent.GetType().GetField("target", BF);
                if (_aiAgentTargetField == null) return true;

                var units = GatherPlayerUnits();

                // Phase RT3-Cousin-arms-Room: out-of-room players (e.g. an AFK team-mate who never entered the arena) and
                // downed players must not be targeted by the arm's group throw — same intent as the downed-untargetable
                // rule. "In room" comes from the ArenaLockdown arena set (host-authoritative + broadcast to clients),
                // which reliably accumulates everyone incl. late walk-ins. When room gating can't be trusted (no active
                // arena membership) `gated` is false and only the downed filter applies (fail-open, so the boss never
                // becomes un-attackable).
                bool filter = false; bool gated = false; System.Collections.Generic.HashSet<string>? members = null;
                try
                {
                    filter = Plugin.Cfg.ExcludeOutOfRoomPlayersFromBossAttacks.Value;
                    if (filter) gated = ArenaLockdownManager.TryGetActiveArenaInRoom(out members);
                }
                catch { }

                int thrown = 0, skippedOutOfRoom = 0;
                if (units.Count > 0)
                {
                    object? prevTarget = _aiAgentTargetField.GetValue(aiAgent);
                    _inThrowLoop = true;
                    try
                    {
                        foreach (var u in units)
                        {
                            if (u == null) continue;
                            if (filter && !IsTargetAttackable(u, gated, members)) { skippedOutOfRoom++; continue; }
                            try { _aiAgentTargetField.SetValue(aiAgent, u); } catch { continue; }
                            try { _throwMethod!.Invoke(__instance, null); thrown++; }
                            catch (Exception ex) { Plugin.Log.Warn($"[ArmThrow] per-player throw failed: {ex.GetType().Name}: {ex.Message}"); }
                        }
                    }
                    finally
                    {
                        _inThrowLoop = false;
                        try { _aiAgentTargetField.SetValue(aiAgent, prevTarget); } catch { }
                    }
                }
                else
                {
                    // No player list resolved — fall back to the native single throw (ensure it has a target).
                    object? cur = _aiAgentTargetField.GetValue(aiAgent);
                    if (cur == null || (cur is UnityEngine.Object uo && uo == null)) return true;
                }

                // CLIENT: GameManager.Players only had the local player (ghosts are host-only), so the loop above threw
                // one ball at us. Add a visual-only ball toward each remote player's proxy position so the client sees the
                // boss attacking everyone. (The host already covers all players via its ghost Players entries above.)
                int remoteVisual = 0, remoteSkippedOutOfRoom = 0;
                if (client)
                {
                    object instCapture = __instance;
                    object? npcCapture = npc;
                    bool fl = filter; bool gt = gated; var mem = members;
                    NetGameplaySyncBridge.ForEachRemotePlayerPositionWithPeer((peerId, pos) =>
                    {
                        // Skip the visual ball toward a remote team-mate who is downed or out of the boss arena.
                        if (fl)
                        {
                            if (NetPlayerLifeManager.IsPeerDownOrDead(peerId)) { remoteSkippedOutOfRoom++; return; }
                            if (gt && !(mem != null && mem.Contains(peerId))) { remoteSkippedOutOfRoom++; return; }
                        }
                        if (ClientVisualThrowAt(instCapture, npcCapture, pos)) remoteVisual++;
                    });
                }

                if (Plugin.Cfg.LogBossDynamicSpawn.Value)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _lastLogAt > 0.5f) { _lastLogAt = now; Plugin.Log.Info($"[ArmThrow] {(client ? "client" : "host")} AoE throw players={units.Count} thrown={thrown} skippedOutOfRoom={skippedOutOfRoom} remoteVisual={remoteVisual} remoteSkippedOutOfRoom={remoteSkippedOutOfRoom} gated={gated} members=[{(members == null ? "" : string.Join(",", members))}]"); }
                }
                return false; // we issued all throws; skip the native single throw
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ArmThrow] ThrowProjectile_Pre failed: {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        // Client-only: spawn a visual-only mud ball (the arm's own visualProjectile, 0 damage) on a ballistic arc toward
        // a world position. Replicates the math of CousinArm.ThrowProjectile so the arc + visual match, minus damage.
        private static bool ClientVisualThrowAt(object cousinArm, object? npcObj, Vector3 targetPos)
        {
            try
            {
                var barrel = _barrelField?.GetValue(cousinArm) as Transform;
                var heightCurve = _heightCurveField?.GetValue(cousinArm) as AnimationCurve;
                var forceCurve = _forceCurveField?.GetValue(cousinArm) as AnimationCurve;
                var visual = _visualField?.GetValue(cousinArm) as ProjectileCustomVisuals;
                var prefabOnHit = _prefabOnHitField?.GetValue(cousinArm) as AutoPooledObject;
                if (barrel == null || heightCurve == null || forceCurve == null) return false;
                var npc = npcObj as Unit;
                if (npc == null) return false;

                const float capsuleHeightFallback = 2f; // we don't resolve each remote proxy's collider; vanilla default
                Vector3 aimAt = targetPos + Vector3.up * capsuleHeightFallback;
                float time = Vector3.Distance(barrel.position, targetPos);
                float h = heightCurve.Evaluate(time);
                Vector3 normalized = (aimAt + Vector3.up * h - barrel.position).normalized;

                var ray = new ProjectileRay(barrel.position, ProjectileTypes.Custom);
                ray.gravity = ProjectileRay.GRAVITY;
                ray.radius = 0.05f;
                ray.startTime = Time.time;
                ray.velocity = forceCurve.Evaluate(time) * normalized;
                var hitmesh = GetMember(npcObj!, "hitmeshCollider") as Collider; // hitmeshCollider lives on Npc (Gameplay)
                ray.ownerInstID = hitmesh != null ? hitmesh.GetInstanceID() : 0;
                ray.drawDefaultBullet = false;

                var data = new ProjectileData
                {
                    explicitDamage = 0f,
                    spawnOnDestroy = prefabOnHit,
                    damageType = DamageTypes.Normal,
                    sourceUnit = npc,
                };
                ProjectileSystem.Instance.StartProjectile(ray, data, visual);
                return true;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ArmThrow] client visual throw-at failed: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        // Phase RT3-Cousin-arms-Room: may the arm's group throw target this player Unit? Excludes downed players and (when
        // arena gating is active) players who are not in the boss arena.
        //  - A host ghost / target-proxy Unit maps to its owning client's peerId → downed via NetPlayerLifeManager, arena
        //    via the broadcast/authoritative ArenaLockdown member set.
        //  - Any other Unit is THIS end's local player (host's real Unit on the host, the local player on the client) →
        //    downed via the local-player check, arena via the reliable local doorway-parity signal.
        // Fail-open (return true) on any error so a resolution failure never makes the boss un-attackable.
        // Internal: reused by the Desert P1 pike-shooting target rotation (same in-room/downed rules).
        internal static bool IsTargetAttackable(object playerUnit, bool gated, System.Collections.Generic.HashSet<string>? members)
        {
            try
            {
                bool isProxy = RemotePlayerTargetProxyManager.TryGetProxyPeer(playerUnit, out string peerId);
                // The local player's own id is the local peer id; a ghost/proxy carries its owning client's id.
                if (!isProxy) peerId = NetGameplaySyncBridge.LocalPeerId;
                // Downed players are never targeted (matches the downed-untargetable rule for ordinary enemies).
                if (isProxy) { if (NetPlayerLifeManager.IsPeerDownOrDead(peerId)) return false; }
                else if (NetPlayerLifeManager.IsDownedLocalPlayerUnit(playerUnit)) return false;
                // Out-of-arena players are not targeted (only when an active arena membership is known). The membership set
                // contains the local player too (each end adds itself on the reliable seal-trigger crossing), so we use it
                // uniformly — the per-arena doorway parity is too fragile (it miscounts re-entries → false "outside").
                if (gated) return members != null && members.Contains(peerId);
                return true;
            }
            catch { return true; }
        }

        private static readonly List<object> _unitScratch = new List<object>(4);
        // Internal: reused by the Desert P1 pike-shooting target rotation (host + ghost player Units).
        internal static List<object> GatherPlayerUnits()
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

        // Phase RT3-Cousin-arms-Room: does this AiAgent belong to a Cousin arm? Used to exempt the arm from the
        // downed-untargetable null (ReverseProbePatches) so it keeps throwing when the local player is downed. Checks the
        // AiAgent's own GameObject + ancestors + descendants for the CousinArm script (precise — won't match sibling
        // henchmen under a shared boss root).
        public static bool IsCousinArmAiAgent(object? aiAgent)
        {
            try
            {
                if (!(aiAgent is Component c) || c == null) return false;
                var armType = _cousinArmType
                    ?? (_cousinArmType = FindGameType("PerfectRandom.Sulfur.Gameplay.CousinArm")
                                      ?? FindGameType("PerfectRandom.Sulfur.Core.CousinArm")
                                      ?? FindGameType("CousinArm"));
                if (armType == null) return false;
                if (c.GetComponentInParent(armType) != null) return true;
                return c.GetComponentInChildren(armType, true) != null;
            }
            catch { return false; }
        }

        private static Type? FindGameType(string fullName)
        {
            try { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(fullName); if (t != null) return t; } } catch { }
            return null;
        }
    }
}
