using System;
using System.Linq;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.Networking.Gameplay.TeamMarks;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Team marks (issue #26): the host's own direct hits. A prefix on <c>Npc.ReceiveDamage</c> scales the damage by the
    /// other players' marks on that enemy, and the postfix places or refreshes the host's own mark when the game applied
    /// the hit. A direct hit is the local player's (<c>isPlayer</c>), names a body part (DoT, Crusader, Voodoo and thorns
    /// use the part-less Default hitbox) and is not an explosion. A client's hit never passes here: it runs inside
    /// <see cref="RemoteHitResolver"/> with <c>isPlayer</c> false and is handled in <c>SettleRosterHit</c>.
    ///
    /// <para>On the host every damage call also opens a scope holding the multiplier it was dealt with (1 for anything
    /// but a boosted direct hit), so <see cref="CurrentHostHitMultiplier"/> answers for the call that is running: damage
    /// the game deals from inside a boosted hit (Crusader's Holy damage on the enemy, thorns on the player) reads 1.
    /// <c>Npc.ReceiveDamage</c> opens it for an NPC; <c>Unit.ReceiveDamage</c>, which the NPC override calls as its
    /// base, opens it only for other units. The scope also lets the thorns reflection of a boosted hit be taken back to
    /// what the unboosted hit would reflect. Lifesteal, Voodoo shares and Crusader's Holy damage follow the boosted
    /// damage, as a reward for focusing.</para>
    /// </summary>
    internal static class TeamMarkPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var target = AccessTools.GetDeclaredMethods(typeof(Npc)).FirstOrDefault(m =>
                {
                    if (m.Name != "ReceiveDamage" || m.ReturnType != typeof(bool)) return false;
                    var p = m.GetParameters();
                    return p.Length == 4 && p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(DamageSourceData)
                        && p[2].ParameterType == typeof(Hitmesh.Data) && p[3].ParameterType == typeof(Vector3?);
                });
                if (target == null) { Plugin.Log.Warn("[TeamMark] Npc.ReceiveDamage not found — the host's own hits neither place nor use team marks"); return; }
                harmony.Patch(target,
                    prefix:    new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(ReceiveDamage_Pre))),
                    postfix:   new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(ReceiveDamage_Post))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(ReceiveDamage_Finalizer))));
                Plugin.Log.Info("[TeamMark] Patched Npc.ReceiveDamage (host's own hits).");

                var unitReceiveDamage = AccessTools.GetDeclaredMethods(typeof(Unit)).FirstOrDefault(m =>
                {
                    if (m.Name != "ReceiveDamage" || m.ReturnType != typeof(bool)) return false;
                    var p = m.GetParameters();
                    return p.Length == 4 && p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(DamageSourceData)
                        && p[2].ParameterType == typeof(Hitmesh.Data) && p[3].ParameterType == typeof(Vector3?);
                });
                if (unitReceiveDamage != null)
                    harmony.Patch(unitReceiveDamage,
                        prefix:    new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(UnitReceiveDamage_Pre))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(UnitReceiveDamage_Finalizer))));
                else
                    Plugin.Log.Warn("[TeamMark] Unit.ReceiveDamage not found — damage to a non-NPC inside a boosted hit reports that hit's multiplier");

                // Tougher enemies (health): both reads of max health. GetAttribute is what health bars, spawn health and
                // AddModifier use; TryGetAttribute is what a punish/kill volume uses to deal exactly max health.
                var getAttribute = AccessTools.DeclaredMethod(typeof(EntityStats), "GetAttribute", new[] { typeof(EntityAttributes) });
                var tryGetAttribute = AccessTools.DeclaredMethod(typeof(EntityStats), "TryGetAttribute", new[] { typeof(EntityAttributes), typeof(float).MakeByRefType() });
                if (getAttribute != null)
                    harmony.Patch(getAttribute, postfix: new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(GetAttribute_Post))));
                if (tryGetAttribute != null)
                    harmony.Patch(tryGetAttribute, postfix: new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(TryGetAttribute_Post))));
                if (getAttribute == null || tryGetAttribute == null)
                    Plugin.Log.Warn($"[TeamMark] max-health reads not found (GetAttribute={getAttribute != null}, TryGetAttribute={tryGetAttribute != null}) — tougher enemies (health) is incomplete");

                ApplyMutationBoost(harmony);
            }
            catch (Exception ex) { Plugin.Log.Error($"[TeamMark] Apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- tougher enemies: mutations

        // FinalizeAndMutateUnitsNode.Execute is a coroutine whose body (count = units x mutationChance, then the node's own
        // RNG picks which units mutate and how) runs inside one MoveNext. The node is a shared generator asset, so the
        // chance is raised only for the duration of that MoveNext and put back by the finalizer, whatever happens.
        private static System.Reflection.FieldInfo? _mutationChanceField, _nodeThisField;

        private static void ApplyMutationBoost(Harmony harmony)
        {
            var nodeType = AccessTools.TypeByName("LevelGeneration.FinalizeAndMutateUnitsNode");
            var execute = nodeType == null ? null : AccessTools.DeclaredMethod(nodeType, "Execute");
            var moveNext = execute == null ? null : AccessTools.EnumeratorMoveNext(execute);
            _mutationChanceField = nodeType == null ? null : AccessTools.Field(nodeType, "mutationChance");
            _nodeThisField = moveNext == null ? null : AccessTools.Field(moveNext.DeclaringType, "<>4__this");
            if (moveNext == null || _mutationChanceField == null || _nodeThisField == null)
            {
                Plugin.Log.Warn($"[TeamMark] mutation node not found (type={nodeType != null}, MoveNext={moveNext != null}, field={_mutationChanceField != null}, this={_nodeThisField != null}) — tougher enemies (mutations) is inactive");
                return;
            }
            harmony.Patch(moveNext,
                prefix:    new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(MutateMoveNext_Pre))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(TeamMarkPatches), nameof(MutateMoveNext_Finalizer))));
        }

        // __state: the node's own chance to put back, or NaN when nothing was changed.
        private static void MutateMoveNext_Pre(object __instance, out float __state)
        {
            __state = float.NaN;
            float factor = CoopBalance.LevelMutationFactor;
            if (factor == 1f) return;
            try
            {
                var node = _nodeThisField!.GetValue(__instance);
                if (node == null) return;
                float original = (float)_mutationChanceField!.GetValue(node);
                if (original <= 0f) return; // a generator with no mutations stays that way
                float boosted = Math.Min(1f, original * factor);
                _mutationChanceField.SetValue(node, boosted);
                __state = original;
                Plugin.Log.Info($"[Balance] mutation chance {original:0.##} -> {boosted:0.##} for this level");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Balance] mutation boost failed: {ex.Message}"); }
        }

        private static Exception? MutateMoveNext_Finalizer(object __instance, float __state, Exception? __exception)
        {
            if (!float.IsNaN(__state))
            {
                try { var node = _nodeThisField!.GetValue(__instance); if (node != null) _mutationChanceField!.SetValue(node, __state); } catch { }
            }
            return __exception;
        }

        // ----------------------------------------------------------------- host: the multiplier of the running call

        // One scope per damage call on the host. Reported = what NetCombat.LocalHostHitMultiplier answers inside it (the
        // host player's own boosted hit only); Applied and Target = the boost this call's damage carries and the enemy that
        // took it, for a host or a remote peer's hit alike, so the thorns correction below can undo it.
        private struct Scope { public float Reported, Applied; public Unit? Target; }

        private const int MaxScopeDepth = 64;
        private static readonly Scope[] _scope = new Scope[MaxScopeDepth];
        private static int _scopeDepth;

        // A remote peer's boosted hit: SettleRosterHit scales the raw damage before Resolve and arms this, so the
        // outermost damage call on that enemy records the boost (its reported multiplier stays 1).
        private static Unit? _remoteBoostTarget;
        private static float _remoteBoost = 1f;

        /// <summary>HOST: the team multiplier the innermost running damage call was dealt with; 1 outside one, inside any
        /// call but a boosted direct hit of the host player's, and on a client or offline.</summary>
        public static float CurrentHostHitMultiplier => _scopeDepth > 0 ? _scope[_scopeDepth - 1].Reported : 1f;

        /// <summary>HOST: a remote peer's hit on <paramref name="target"/> was scaled by <paramref name="boost"/> before
        /// settling; call <see cref="DisarmRemoteBoost"/> once it is settled.</summary>
        internal static void ArmRemoteBoost(Unit target, float boost) { _remoteBoostTarget = target; _remoteBoost = boost; }
        internal static void DisarmRemoteBoost() { _remoteBoostTarget = null; _remoteBoost = 1f; }

        private static bool PushScope(float reported, float applied, Unit? target)
        {
            if (_scopeDepth >= MaxScopeDepth) return false; // runaway recursion: leave the innermost known scope
            _scope[_scopeDepth++] = new Scope { Reported = reported, Applied = applied, Target = target };
            return true;
        }

        private static void PopScope()
        {
            if (_scopeDepth > 0) _scope[--_scopeDepth] = default;
        }

        private const int StateDirect = 1, StateBoosted = 2, StateScope = 4;

        // __state: StateDirect = a direct hit on an eligible enemy, StateBoosted = it took the bonus, StateScope = this call
        // opened a multiplier scope (closed by the finalizer, which runs whatever happens).
        private static void ReceiveDamage_Pre(Npc __instance, ref float damage, DamageSourceData source, Hitmesh.Data hitbox, out int __state)
        {
            __state = 0;
            if (!NetGameplaySyncBridge.IsHost) return;
            float reported = 1f, applied = 1f;
            try
            {
                if (RemoteHitResolver.IsResolving)
                {
                    if (_remoteBoostTarget != null && ReferenceEquals(_remoteBoostTarget, __instance))
                    {
                        applied = _remoteBoost;
                        _remoteBoostTarget = null; // only the outermost call carries it
                    }
                }
                else if (source.isPlayer && source.damageType != DamageTypes.Explosive)
                {
                    var part = hitbox.shapeId.part;
                    if (part != HitboxColliders.Parts.None && part != HitboxColliders.Parts.Block
                        && TeamMarkManager.TryGetEligibleIndex(__instance, out _))
                    {
                        __state |= StateDirect;
                        float mult = TeamMarkManager.GetMultiplier(__instance, NetGameplaySyncBridge.LocalPeerId);
                        if (mult > 1f)
                        {
                            damage *= mult;
                            reported = applied = mult;
                            __state |= StateBoosted;
                        }
                    }
                }
            }
            catch { __state = 0; reported = applied = 1f; }
            if (PushScope(reported, applied, __instance)) __state |= StateScope;
        }

        private static Exception? ReceiveDamage_Finalizer(Exception? __exception, int __state)
        {
            if ((__state & StateScope) != 0) PopScope();
            return __exception;
        }

        // Any unit's damage call (an NPC's override reaches it as its base call).
        // 1. Thorns: the game reflects onto the attacker a share of the damage the enemy received, and a boosted hit's
        //    damage already includes the team bonus. The bonus is a reward for focusing, so the reflection is taken back
        //    to what the unboosted hit would have reflected. It is the call the boosted enemy makes, from inside its own
        //    damage call, on another unit, with Physics damage and no hitbox.
        // 2. Any other unit than an NPC (the player, a non-NPC unit) gets a scope of 1, so damage dealt to it from inside
        //    a boosted hit does not report that hit's multiplier. An NPC's own override opened its scope already.
        private static void UnitReceiveDamage_Pre(Unit __instance, ref float damage, DamageSourceData sourceData, Hitmesh.Data hitbox, out bool __state)
        {
            __state = false;
            if (!NetGameplaySyncBridge.IsHost) return;
            try
            {
                if (_scopeDepth > 0)
                {
                    var top = _scope[_scopeDepth - 1];
                    if (top.Applied > 1f && top.Target != null && !ReferenceEquals(__instance, top.Target)
                        && ReferenceEquals(sourceData.sourceUnit, top.Target)
                        && sourceData.damageType == DamageTypes.Physics && hitbox.shapeId.part == HitboxColliders.Parts.None)
                        damage /= top.Applied;
                }
            }
            catch { }
            __state = !(__instance is Npc) && PushScope(1f, 1f, __instance);
        }

        private static Exception? UnitReceiveDamage_Finalizer(Exception? __exception, bool __state)
        {
            if (__state) PopScope();
            return __exception;
        }

        // Tougher enemies (health): the level's factor for ordinary enemies (TeamMarkManager.HealthFactorFor).
        private static void GetAttribute_Post(EntityStats __instance, EntityAttributes id, ref float __result)
        {
            if (id != EntityAttributes.Stat_MaxHealth || CoopBalance.LevelHealthFactor == 1f) return;
            try { __result *= CoopBalance.HealthFactorFor(__instance.owner); } catch { }
        }

        private static void TryGetAttribute_Post(EntityStats __instance, EntityAttributes id, ref float value, bool __result)
        {
            if (!__result || id != EntityAttributes.Stat_MaxHealth || CoopBalance.LevelHealthFactor == 1f) return;
            try { value *= CoopBalance.HealthFactorFor(__instance.owner); } catch { }
        }

        // Only a hit the game applied counts (a parry, an invulnerable part or a petrified target returns false).
        private static void ReceiveDamage_Post(Npc __instance, bool __result, int __state)
        {
            if ((__state & StateDirect) == 0 || !__result) return;
            try
            {
                if ((__state & StateBoosted) != 0) { TeamMarkManager.HostHitsBoosted++; TeamMarkFeedback.Play(__instance, Time.frameCount); }
                TeamMarkManager.OnDirectHit(NetGameplaySyncBridge.LocalPeerId, __instance);
            }
            catch { }
        }
    }
}
