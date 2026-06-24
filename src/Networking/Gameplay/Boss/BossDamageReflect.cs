using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-F: invokes the REAL `Unit.ReceiveDamage` so a host-applied boss hit goes through the vanilla
    /// damage pipeline (fires `onDamageRecieved`, which drives every boss mechanic — Cousin.damageUntilSubmerge,
    /// WitchPhase2.RealWitchTakeDamage, Lucia phase health checks, ...). Writing Stats.SetStatus(92) — as the regular
    /// enemy hit path does — does NOT fire that delegate, so it never advances boss mechanics.
    ///
    /// Confirmed by decompilation (PerfectRandom.Sulfur.Core.Units.Unit):
    ///   - `Unit : MonoBehaviour, IDamager`.
    ///   - overload `bool ReceiveDamage(float damage, DamageTypes damageType, IDamager source, Hitmesh.Data hitbox, Vector3? collisionPoint)`
    ///     wraps `new DamageSourceData(source)` and dispatches to the real DamageSourceData overload.
    ///   - `Hitmesh.Data.Default` is a public static readonly field.
    /// We resolve the exact enum/struct/interface types FROM the method signature at runtime (no hard-coded type
    /// names), so this stays correct even if namespaces differ. The host's PlayerUnit is the IDamager source.
    /// </summary>
    internal static class BossDamageReflect
    {
        private static readonly object _resolveLock = new object();
        private static bool _resolved;
        private static MethodInfo? _receiveDamage;   // the IDamager overload
        private static Type? _damageTypesEnum;       // param[1]
        private static Type? _damagerInterface;      // param[2]
        private static object? _hitmeshDefault;      // Hitmesh.Data.Default (param[3])

        // GameManager.Instance.PlayerUnit resolution (the real IDamager source for a player hit).
        private static PropertyInfo? _gmInstanceProp;
        private static PropertyInfo? _gmPlayerUnitProp;
        private static bool _gmResolved;

        /// <summary>The host's local player Unit (`GameManager.Instance.PlayerUnit`), which is a `Unit : IDamager` —
        /// the correct damage source. Earlier we passed `Player` (PlayerScript), which is NOT an IDamager.</summary>
        public static object? ResolveHostPlayerUnit()
        {
            try
            {
                if (!_gmResolved)
                {
                    _gmResolved = true;
                    var gmType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                    if (gmType != null)
                    {
                        // Instance may be inherited from a StaticInstance<T> base — AccessTools walks the hierarchy.
                        _gmInstanceProp = HarmonyLib.AccessTools.Property(gmType, "Instance");
                        _gmPlayerUnitProp = HarmonyLib.AccessTools.Property(gmType, "PlayerUnit");
                    }
                }
                var gm = _gmInstanceProp?.GetValue(null, null);
                if (gm == null) return null;
                return _gmPlayerUnitProp?.GetValue(gm, null);
            }
            catch { return null; }
        }

        /// <summary>True if the resolved ReceiveDamage source-parameter interface is implemented by <paramref name="o"/>.</summary>
        public static bool IsValidDamageSource(object? o) => o != null && _damagerInterface != null && _damagerInterface.IsInstanceOfType(o);

        public static string DamagerInterfaceName => _damagerInterface?.Name ?? "<unresolved>";
        public static string ReceiveDamageSignature
            => _receiveDamage == null ? "<unresolved>"
             : $"{_receiveDamage.DeclaringType?.Name}.ReceiveDamage({string.Join(",", _receiveDamage.GetParameters().Select(p => p.ParameterType.Name))})";

        private static void ResolveFor(object unit)
        {
            if (_resolved) return;
            lock (_resolveLock)
            {
                if (_resolved) return;
                try
                {
                    // Find ReceiveDamage(float, DamageTypes, IDamager, Hitmesh.Data, Vector3?) — the source param is
                    // an interface (IDamager); the sibling overload takes a DamageSourceData struct/class instead.
                    MethodInfo? best = null;
                    for (Type? t = unit.GetType(); t != null && best == null; t = t.BaseType)
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.Name != "ReceiveDamage") continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 5) continue;
                            if (ps[0].ParameterType != typeof(float)) continue;
                            if (!ps[1].ParameterType.IsEnum) continue;
                            if (!ps[2].ParameterType.IsInterface) continue; // IDamager overload
                            best = m; break;
                        }
                    }
                    if (best != null)
                    {
                        var ps = best.GetParameters();
                        _receiveDamage = best;
                        _damageTypesEnum = ps[1].ParameterType;
                        _damagerInterface = ps[2].ParameterType;
                        var hitboxType = ps[3].ParameterType;
                        var defField = hitboxType.GetField("Default", BindingFlags.Public | BindingFlags.Static);
                        _hitmeshDefault = defField?.GetValue(null);
                        Plugin.Log.Info($"[BossDamage] resolved real ReceiveDamage: {best.DeclaringType?.Name}.ReceiveDamage(float,{_damageTypesEnum.Name},{_damagerInterface.Name},{hitboxType.Name},Vector3?) hitmeshDefault={(_hitmeshDefault != null ? "ok" : "null")}");
                    }
                    else
                    {
                        Plugin.Log.Warn("[BossDamage] could not resolve the IDamager ReceiveDamage overload.");
                    }
                }
                catch (Exception ex) { Plugin.Log.Warn($"[BossDamage] resolve failed: {ex.GetType().Name}: {ex.Message}"); }
                _resolved = true;
            }
        }

        /// <summary>Apply <paramref name="damage"/> to <paramref name="targetUnit"/> through the real ReceiveDamage,
        /// using the host's player as the IDamager source. Returns the vanilla bool result (false = invulnerable /
        /// parried / no damage). <paramref name="detail"/> explains the outcome.</summary>
        public static bool TryApplyRealDamage(object targetUnit, float damage, int damageTypeInt, object? sourcePlayerUnit, out bool vanillaResult, out string detail)
        {
            vanillaResult = false; detail = "";
            if (targetUnit == null) { detail = "null-target"; return false; }
            ResolveFor(targetUnit);
            if (_receiveDamage == null || _damageTypesEnum == null) { detail = "receiveDamage-unresolved"; return false; }
            if (_hitmeshDefault == null) { detail = "hitmesh-default-unresolved"; return false; }

            // Source must be assignable to IDamager. The host PlayerUnit (a Unit : IDamager) is the natural source so
            // DamageSourceData.factionId becomes Player and `damagedByPlayer` is set (matches a real player hit).
            if (sourcePlayerUnit == null || !_damagerInterface!.IsInstanceOfType(sourcePlayerUnit))
            { detail = $"source-not-IDamager ({(sourcePlayerUnit == null ? "null" : sourcePlayerUnit.GetType().Name)})"; return false; }

            try
            {
                object damageType = Enum.ToObject(_damageTypesEnum, damageTypeInt);
                object? result = _receiveDamage.Invoke(targetUnit, new object?[] { damage, damageType, sourcePlayerUnit, _hitmeshDefault, (Vector3?)null });
                vanillaResult = result is bool b && b;
                detail = $"ReceiveDamage returned {vanillaResult}";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                detail = $"ReceiveDamage threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (Exception ex) { detail = $"invoke failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }
    }
}
