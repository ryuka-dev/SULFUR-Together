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

            // ST-3-DIFF: diagnostic-only postfixes on the ONLY two methods that touch the frozen animator bool —
            // ApplyEffect sets it, RemoveEffect clears it. Neither postfix changes anything; they record what ran.
            var applyEffect  = AccessTools.DeclaredMethod(typeof(AttributeEffect), "ApplyEffect");
            var removeEffect = AccessTools.DeclaredMethod(typeof(AttributeEffect), "RemoveEffect");
            if (applyEffect != null)
                harmony.Patch(applyEffect, postfix: new HarmonyMethod(
                    typeof(UnitStatusPatches).GetMethod(nameof(ApplyEffect_Post), BindingFlags.Static | BindingFlags.NonPublic)));
            if (removeEffect != null)
                harmony.Patch(removeEffect, postfix: new HarmonyMethod(
                    typeof(UnitStatusPatches).GetMethod(nameof(RemoveEffect_Post), BindingFlags.Static | BindingFlags.NonPublic)));
            Plugin.Log.Info($"[UnitStatus] Patched AttributeEffect.ApplyEffect({applyEffect != null})/RemoveEffect({removeEffect != null}) (frozen transition trail, diagnostic).");
        }

        /// <summary>Frozen effect coroutines counted on the way in, so the postfix can tell whether the shatter branch
        /// cleared them. Only ever holds a value across one synchronous call, but vanilla re-enters itself, so it is a
        /// stack rather than a field.</summary>
        private static readonly Stack<int> _frozenCoroutinesOnEntry = new Stack<int>();

        private static bool ReApplyEffect_Pre(AttributeEffect __instance, Unit unit, float newValue)
        {
            try
            {
                if (unit == null) return true;

                // ST-3e. Host: remember how many effect coroutines the Frozen status is holding. The shatter branch
                // stops and clears them before it pins the value, and that is the only synchronous fingerprint the
                // roll leaves — the status write it makes can be a 100→100 no-op that no edge ever carries.
                if (__instance.id == EntityAttributes.NegativeEffect_Frozen && NetConfig.GetMode() == NetMode.Host)
                    _frozenCoroutinesOnEntry.Push(UnitStatusSyncManager.CountFrozenEffectUpdates(unit));

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

        // ST-3-DIFF. Diagnostic only: record which of the two bool-owning methods ran, with the arguments that decide
        // whether RemoveEffect takes its early return, and the state of the animator bool afterwards.
        private static void ApplyEffect_Post(AttributeEffect __instance, Unit unit, float attrValue)
        {
            if (__instance.id != EntityAttributes.NegativeEffect_Frozen || unit is not Npc npc) return;
            FrozenSolidDiffProbe.NoteTransition(unit, $"A{attrValue:F0}:{AnimBool(__instance, npc)}");
        }

        private static void RemoveEffect_Post(AttributeEffect __instance, Unit unit, bool died, bool forceFullRemove, float removeValue)
        {
            if (__instance.id != EntityAttributes.NegativeEffect_Frozen || unit is not Npc npc) return;
            FrozenSolidDiffProbe.NoteTransition(unit,
                $"R(d={(died ? 'T' : 'F')},f={(forceFullRemove ? 'T' : 'F')},v={removeValue:F0}):{AnimBool(__instance, npc)}");
        }

        /// <summary>The animator bool this effect owns, read back after the call. '-' when the effect declares none.</summary>
        private static string AnimBool(AttributeEffect effect, Npc npc)
        {
            try
            {
                string param = ResolveActiveEffect(effect, npc).animatorParameter;
                if (string.IsNullOrEmpty(param)) return "-";
                var a = npc.animator;
                return a == null ? "?" : (a.GetBool(param) ? "T" : "F");
            }
            catch { return "?"; }
        }

        private static void ReApplyEffect_Post(AttributeEffect __instance, Unit unit)
        {
            if (unit == null || __instance.id != EntityAttributes.NegativeEffect_Frozen) return;
            if (_frozenCoroutinesOnEntry.Count == 0) return;   // client side never pushed, or the original threw

            try
            {
                int before = _frozenCoroutinesOnEntry.Pop();
                // A Harmony postfix does not run when the original throws, so a leaked entry would shift every later
                // pair by one. Nothing legitimately nests this deep.
                if (_frozenCoroutinesOnEntry.Count > 8) _frozenCoroutinesOnEntry.Clear();

                if (before > 0 && UnitStatusSyncManager.CountFrozenEffectUpdates(unit) == 0)
                    UnitStatusSyncManager.ReportHostFrozenSolidShatter(unit);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ReApplyEffect_Post failed: {ex.GetType().Name}: {ex.Message}");
            }
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

        /// <summary>The Frozen effect asset that applies to this unit, resolved the way <c>Unit.OnStatusUpdated</c>
        /// resolves it for an NPC, plus the unit's own override. Null if the asset tables are not up yet.</summary>
        internal static AttributeEffect? ResolveFrozenEffect(Unit unit)
        {
            try
            {
                EntityAttribute asset = EntityAttributes.NegativeEffect_Frozen.GetAsset();
                AttributeEffect? baseEffect = asset != null ? asset.effectSettings.GetAttributeEffects() : null;
                return baseEffect == null ? null : ResolveActiveEffect(baseEffect, unit);
            }
            catch { return null; }
        }

        /// <summary>ST-3-DIFF helper: the shader float the frost coverage is written to for this unit.</summary>
        internal static string? GetFrozenShaderParameter(Unit unit) => ResolveFrozenEffect(unit)?.shaderParameter;

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
                AttributeEffect? effect = ResolveFrozenEffect(npc);
                if (effect == null) { detail = "no AttributeEffect for Frozen"; return false; }

                if (_delayedFrozenSolid.Invoke(effect, new object[] { npc }) is not IEnumerator routine)
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
