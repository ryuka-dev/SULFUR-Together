using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.ReverseProbe;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Issue #2 diagnostic probe — ground-hazard / damage-over-time (DoT) status on the LOCAL player. PURE DIAGNOSTIC:
    /// no functional behavior, everything gated behind <see cref="CoopConfig.LogHazardProbe"/> (default OFF).
    ///
    /// <para>Background (verified against the decompiled game): a poison/fire "splatter" does not deal damage directly —
    /// it raises a negative-effect STATUS on whoever stands in it. That status drives the game's <c>AttributeEffect</c>
    /// DoT coroutine, which ticks <c>Unit.ReceiveDamage</c> with damage scaled by the current status magnitude. The
    /// status value is hard-capped per player (Poisoned/Burning/Bleed/Electrocuted all cap at 7 in EntityStats), so the
    /// reported client-side "poison stacks infinitely, instantly downs" runaway cannot come from the value climbing —
    /// the prime suspect is MULTIPLE concurrent DoT coroutines (each 0→+ edge starts one and appends it to
    /// <c>Unit.effectUpdates[id]</c>), i.e. many discrete/overlapping hazard sources on the client (ground effects are
    /// not synced today).</para>
    ///
    /// <para>This probe answers two questions:</para>
    /// <list type="number">
    /// <item>WHERE it comes from — <c>RegisterAppliedStatus(EntityAttributes, Unit)</c> postfix logs the applying unit's
    /// identity (player weapon/self vs. enemy Npc vs. other) whenever a damaging status is registered on the local player.</item>
    /// <item>WHY it escalates — <c>OnStatusUpdated</c> postfix logs every damaging-status change on the local player: the
    /// edge kind (Apply 0→+, Inc, Tick/Dec, Remove +→0), the live coroutine count for that status, and current HP.</item>
    /// </list>
    /// </summary>
    internal static class HazardProbePatches
    {
        // EntityAttributes int values (enum : ushort, None = 0) for the damaging statuses a player accumulates.
        // Verified against the decompiled EntityAttributes enum and the per-player status caps in EntityStats (all 7).
        private const int Status_Burning      = 19;
        private const int Status_Electrocuted = 22;
        private const int Status_Poisoned     = 28;
        private const int Status_Bleed        = 102;

        private static FieldInfo? _effectUpdatesField;   // Unit.effectUpdates : Dictionary<EntityAttributes, List<Coroutine>>

        public static void Apply(Harmony harmony)
        {
            try
            {
                var unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
                if (unitType == null)
                {
                    Plugin.Log.Warn("[Hazard] Unit type not found — hazard probe disabled.");
                    return;
                }

                _effectUpdatesField = AccessTools.Field(unitType, "effectUpdates");
                if (_effectUpdatesField == null)
                    Plugin.Log.Warn("[Hazard] Unit.effectUpdates not found — coroutine count will read -1.");

                var onStatus = AccessTools.DeclaredMethod(unitType, "OnStatusUpdated");
                if (onStatus != null)
                    harmony.Patch(onStatus, postfix: new HarmonyMethod(
                        typeof(HazardProbePatches).GetMethod(nameof(OnStatusUpdated_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[Hazard] Unit.OnStatusUpdated not found — status-edge probe disabled.");

                // The 2-arg RegisterAppliedStatus(EntityAttributes id, Unit source) is the one that records the source;
                // the EntityAttribute overload funnels into it, so patching this one catches both.
                var attrEnum = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Stats.EntityAttributes");
                var reg = (attrEnum != null)
                    ? AccessTools.Method(unitType, "RegisterAppliedStatus", new[] { attrEnum, unitType })
                    : null;
                if (reg != null)
                    harmony.Patch(reg, postfix: new HarmonyMethod(
                        typeof(HazardProbePatches).GetMethod(nameof(RegisterAppliedStatus_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[Hazard] Unit.RegisterAppliedStatus(EntityAttributes,Unit) not found — source probe disabled.");

                Plugin.Log.Info("[Hazard] Patched Unit.OnStatusUpdated + RegisterAppliedStatus (issue #2 hazard probe).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[Hazard] Apply failed: {ex.Message}");
            }
        }

        private static bool IsDamagingStatus(int id) =>
            id == Status_Poisoned || id == Status_Burning || id == Status_Bleed || id == Status_Electrocuted;

        private static string StatusName(int id) => id switch
        {
            Status_Poisoned     => "Poisoned",
            Status_Burning      => "Burning",
            Status_Bleed        => "Bleed",
            Status_Electrocuted => "Electrocuted",
            _                   => id.ToString()
        };

        // Postfix: fires after every status change is committed. Filters to the local player + damaging statuses.
        private static void OnStatusUpdated_Post(object __instance, object id, float prevValue, float newValue)
        {
            try
            {
                if (!Plugin.Cfg.LogHazardProbe.Value) return;
                if (!NetPlayerLifeManager.IsLocalPlayerUnit(__instance)) return;

                int idInt;
                try { idInt = Convert.ToInt32(id); } catch { return; }
                if (!IsDamagingStatus(idInt)) return;

                string edge = (newValue > 0f && prevValue <= 0f) ? "Apply(0->+)"
                            : (newValue <= 0f && prevValue >  0f) ? "Remove(+->0)"
                            : (newValue > prevValue)              ? "Inc(+)"
                            :                                        "Tick/Dec(-)";

                int coroutines = ReadCoroutineCount(__instance, id);
                string hp = NetGameplayProbeManager.TryReadBossUnitHealth(__instance, out float cur, out float max)
                    ? $"{cur:F0}/{max:F0}" : "?";

                Plugin.Log.Info($"[Hazard] t={Time.time:F2} {StatusName(idInt)} {prevValue:F2}->{newValue:F2} {edge} coroutines={coroutines} hp={hp}");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Hazard.Status] {ex.Message}"); }
        }

        // Postfix: fires when a status source is recorded. Names the applier so we know player-weapon vs enemy vs env.
        private static void RegisterAppliedStatus_Post(object __instance, object id, object source)
        {
            try
            {
                if (!Plugin.Cfg.LogHazardProbe.Value) return;
                if (!NetPlayerLifeManager.IsLocalPlayerUnit(__instance)) return;

                int idInt;
                try { idInt = Convert.ToInt32(id); } catch { return; }
                if (!IsDamagingStatus(idInt)) return;

                Plugin.Log.Info($"[Hazard] t={Time.time:F2} {StatusName(idInt)} applied by {DescribeSource(source)}");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Hazard.Source] {ex.Message}"); }
        }

        private static int ReadCoroutineCount(object unit, object id)
        {
            try
            {
                if (_effectUpdatesField?.GetValue(unit) is IDictionary dict && dict.Contains(id) && dict[id] is ICollection list)
                    return list.Count;
            }
            catch { }
            return -1;
        }

        private static string DescribeSource(object? source)
        {
            if (source == null) return "<null source>";
            string who = ReverseProbeFormatter.FormatInstance(source);
            string kind = TryReadBool(source, "isPlayer") ? " [PLAYER weapon/self]"
                        : TryReadBool(source, "isNpc")    ? " [NPC/enemy]"
                        :                                    " [other]";
            return who + kind;
        }

        private static bool TryReadBool(object obj, string member)
        {
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = obj.GetType().GetProperty(member, bf);
                if (prop != null) return prop.GetValue(obj) is bool pb && pb;
                var field = obj.GetType().GetField(member, bf);
                if (field != null) return field.GetValue(obj) is bool fb && fb;
            }
            catch { }
            return false;
        }
    }
}
