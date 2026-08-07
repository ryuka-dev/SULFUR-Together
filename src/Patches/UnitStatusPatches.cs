using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using Unity.Collections;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// ST-1 / ST-2 enemy status-effect authority — the two canonical hooks (see <see cref="UnitStatusSyncManager"/>).
    ///
    /// <para><c>Unit.ApplyHitModifiers</c> prefix (ST-1): the single chokepoint where an attack's on-hit status
    /// modifiers are applied — both the projectile path (<c>ProjectileUtilities.ProcessUnitHit</c>) and the melee path
    /// (<c>Hitmesh</c>) route through it. It is a separate call from <c>ReceiveDamage</c>, which is why the existing
    /// client→host damage channel never carried enchantment effects.</para>
    ///
    /// <para><c>Unit.OnStatusUpdated</c> postfix (ST-2): the callback the game itself uses to raise and remove an
    /// effect's presentation, so it is also the authoritative transition point to broadcast from. High-frequency
    /// (every health change, every decay tick) — the manager's first test rejects non-edges.</para>
    ///
    /// <para><c>AttributeEffect.ReApplyEffect</c> prefix/postfix (ST-3): the one place vanilla decides whether a frozen
    /// unit shatters. On the host it brackets the decision so the resulting status write can be labelled as the
    /// shatter; on a client it takes the decision away and leaves only the presentation. See
    /// <see cref="UnitStatusSyncManager.SuppressClientFrozenSolidRoll"/>.</para>
    ///
    /// <para>The game's <c>FixedList32Bytes&lt;ModifierData&gt;</c> is translated to plain tuples here so the sync
    /// manager stays free of Unity.Collections and game struct types.</para>
    /// </summary>
    internal static class UnitStatusPatches
    {
        // AttributeEffect's two private helpers, resolved once at patch time. Never resolve reflection inside these
        // callbacks: ReApplyEffect runs per status change on every unit, and a per-call lookup in a path at that rate
        // is the EMP-DW mistake.
        private static MethodInfo? _getPotentialOverride;   // AttributeEffect GetPotentialOverride(Unit)
        private static MethodInfo? _updateMovementSpeed;    // void UpdateMovementSpeed(Npc)
        private static MethodInfo? _delayedFrozenSolid;     // IEnumerator DelayedFrozenSolid(Npc)

        public static void Apply(Harmony harmony)
        {
            try
            {
                ApplyFrozenSolidAuthority(harmony);
                var applyHitModifiers = AccessTools.DeclaredMethod(typeof(Unit), "ApplyHitModifiers");
                if (applyHitModifiers != null)
                    harmony.Patch(applyHitModifiers, prefix: new HarmonyMethod(
                        typeof(UnitStatusPatches).GetMethod(nameof(ApplyHitModifiers_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Error("[UnitStatus] Unit.ApplyHitModifiers not found — client enchantment effects will NOT reach the host.");

                var onStatusUpdated = AccessTools.DeclaredMethod(typeof(Unit), "OnStatusUpdated");
                if (onStatusUpdated != null)
                    harmony.Patch(onStatusUpdated, postfix: new HarmonyMethod(
                        typeof(UnitStatusPatches).GetMethod(nameof(OnStatusUpdated_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Error("[UnitStatus] Unit.OnStatusUpdated not found — host status effects will NOT be mirrored to clients.");

                Plugin.Log.Info($"[UnitStatus] Patched Unit.ApplyHitModifiers({applyHitModifiers != null})/OnStatusUpdated({onStatusUpdated != null}) (enemy status effect sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[UnitStatus] Apply failed: {ex.Message}");
            }
        }

        private static bool ApplyHitModifiers_Pre(Unit __instance, FixedList32Bytes<ModifierData> modifiersOnHit, Unit attacker)
        {
            try
            {
                int count = modifiersOnHit.Length;
                if (count <= 0) return true;

                var entries = new List<(ushort Attribute, float Value, float ProcChance)>(count);
                for (int i = 0; i < count; i++)
                {
                    ModifierData data = modifiersOnHit[i];
                    entries.Add(((ushort)data.attribute, data.value, data.procChance));
                }

                return !UnitStatusSyncManager.TryInterceptClientPuppetHitModifiers(__instance, entries, attacker);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ApplyHitModifiers_Pre failed: {ex.GetType().Name}: {ex.Message}");
                return true; // on our own failure, let the vanilla application run
            }
        }

        private static void OnStatusUpdated_Post(Unit __instance, EntityAttributes id, float prevValue, float newValue)
        {
            UnitStatusSyncManager.ReportHostUnitStatusEdge(__instance, id, prevValue, newValue);
        }

        // ----------------------------------------------------------------
        // ST-3 — AttributeEffect.ReApplyEffect: the frozen-solid decision
        // ----------------------------------------------------------------

        private static void ApplyFrozenSolidAuthority(Harmony harmony)
        {
            var reApplyEffect = AccessTools.DeclaredMethod(typeof(AttributeEffect), "ReApplyEffect");
            if (reApplyEffect == null)
            {
                Plugin.Log.Error("[UnitStatus] AttributeEffect.ReApplyEffect not found — clients will roll their OWN frozen-solid on host enemies.");
                return;
            }

            _getPotentialOverride = AccessTools.DeclaredMethod(typeof(AttributeEffect), "GetPotentialOverride");
            _updateMovementSpeed  = AccessTools.DeclaredMethod(typeof(AttributeEffect), "UpdateMovementSpeed");
            _delayedFrozenSolid   = AccessTools.DeclaredMethod(typeof(AttributeEffect), "DelayedFrozenSolid");
            if (_updateMovementSpeed == null)
                Plugin.Log.Warn("[UnitStatus] AttributeEffect.UpdateMovementSpeed not found — a suppressed frozen re-apply will not refresh puppet slow speed.");
            if (_delayedFrozenSolid == null)
                Plugin.Log.Error("[UnitStatus] AttributeEffect.DelayedFrozenSolid not found — a mirrored shatter will not show the ice pose on clients.");

            harmony.Patch(reApplyEffect,
                prefix:  new HarmonyMethod(typeof(UnitStatusPatches).GetMethod(nameof(ReApplyEffect_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                postfix: new HarmonyMethod(typeof(UnitStatusPatches).GetMethod(nameof(ReApplyEffect_Post), BindingFlags.Static | BindingFlags.NonPublic)));

            Plugin.Log.Info("[UnitStatus] Patched AttributeEffect.ReApplyEffect (frozen-solid authority).");
        }

        private static bool ReApplyEffect_Pre(AttributeEffect __instance, Unit unit, float newValue)
        {
            try
            {
                if (unit == null) return true;

                // Host: bracket the shatter decision. Anything this call writes back into the status is attributable
                // to it, which is how the resulting edge learns it is a shatter rather than an ordinary raise.
                if (__instance.id == EntityAttributes.NegativeEffect_Frozen && NetConfig.GetMode() == NetMode.Host)
                    UnitStatusSyncManager.BeginHostFrozenReapply(unit);

                if (!UnitStatusSyncManager.SuppressClientFrozenSolidRoll(unit, __instance.id))
                    return true;

                ApplyFrozenPresentationOnly(__instance, unit, newValue);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ReApplyEffect_Pre failed: {ex.GetType().Name}: {ex.Message}");
                return true; // on our own failure, let the vanilla application run
            }
        }

        private static void ReApplyEffect_Post(AttributeEffect __instance, Unit unit)
        {
            // Unconditional counterpart to the prefix's arm — no mode check, because a mode change between the two
            // would otherwise strand the depth. EndHostFrozenReapply is a no-op for a unit that was never armed.
            if (unit != null && __instance.id == EntityAttributes.NegativeEffect_Frozen)
                UnitStatusSyncManager.EndHostFrozenReapply(unit);
        }

        /// <summary>
        /// Reproduce everything vanilla's frozen <c>ReApplyEffectImp</c> branch does EXCEPT decide the shatter: the
        /// frost coverage on the material and the slow-speed refresh. Both are read straight off the effect asset and
        /// driven through the game's own methods rather than restated, so a balance change to either follows.
        /// </summary>
        private static void ApplyFrozenPresentationOnly(AttributeEffect effect, Unit unit, float newValue)
        {
            if (unit is not Npc npc) return;

            AttributeEffect active = ResolveActiveEffect(effect, unit);

            if (!string.IsNullOrEmpty(active.shaderParameter))
                npc.SetMaterialFloat(active.shaderParameter, newValue / 100f);

            _updateMovementSpeed?.Invoke(active, new object[] { npc });
        }

        /// <summary>An effect asset can be overridden per unit (<c>UnitSO.attributeEffectOverrides</c>), and the
        /// override owns its own <c>shaderParameter</c> and sounds. Vanilla resolves that before touching anything.</summary>
        private static AttributeEffect ResolveActiveEffect(AttributeEffect effect, Unit unit)
        {
            if (_getPotentialOverride != null
                && _getPotentialOverride.Invoke(effect, new object[] { unit }) is AttributeEffect resolved && resolved != null)
                return resolved;
            return effect;
        }

        /// <summary>
        /// ST-3d. Run vanilla's <c>AttributeEffect.DelayedFrozenSolid</c> to completion RIGHT NOW rather than leaving it
        /// queued as a coroutine — the pose, the solid-ice shader time, the shatter sound and the (swallowed) lethal
        /// frost damage, all from the game's own body.
        /// <para>The coroutine opens with <c>yield return null</c>. That costs the host nothing, because there the
        /// coroutine is itself the cause of the death and everything visible is applied before it deals the killing
        /// blow. A client has no such ordering: the status edge and the death event are separate messages that arrive
        /// together, so <c>Die()</c> runs first and the coroutine wakes a frame later against a corpse already playing
        /// its death animation. Driving the same body synchronously removes exactly that frame.</para>
        /// </summary>
        internal static bool TryPlayFrozenSolidNow(Npc npc, out string detail)
        {
            detail = "";
            if (_delayedFrozenSolid == null) { detail = "DelayedFrozenSolid unresolved"; return false; }

            try
            {
                // Same lookup Unit.OnStatusUpdated uses to pick an NPC's effect, then the unit's own override.
                EntityAttribute asset = EntityAttributes.NegativeEffect_Frozen.GetAsset();
                AttributeEffect? baseEffect = asset != null ? asset.effectSettings.GetAttributeEffects() : null;
                if (baseEffect == null) { detail = "no AttributeEffect for Frozen"; return false; }

                if (_delayedFrozenSolid.Invoke(ResolveActiveEffect(baseEffect, npc), new object[] { npc }) is not IEnumerator routine)
                { detail = "DelayedFrozenSolid returned no enumerator"; return false; }

                // Bounded: vanilla's body is one `yield return null` and then a straight run to the end. The cap only
                // stops a future version that started waiting on something from being spun here.
                const int MaxSteps = 8;
                int steps = 0;
                while (routine.MoveNext())
                {
                    if (++steps >= MaxSteps) { detail = $"enumerator still running after {steps} steps"; return false; }
                }

                detail = $"ran synchronously in {steps + 1} step(s)";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }
}
