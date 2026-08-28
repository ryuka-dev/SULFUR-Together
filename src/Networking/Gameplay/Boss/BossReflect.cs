using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: small defensive reflection helpers shared by Boss adapters. Discovery-first: if a member
    /// is missing the helpers return null/false rather than throwing, so adapters never crash the game.
    /// </summary>
    internal static class BossReflect
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        // Walk most-derived -> base ourselves with DeclaredOnly so `new` (member-hiding) members resolve to the
        // most-derived one. LuciaBossFightHelper declares `public new void TriggerFight()` hiding the base virtual;
        // a non-DeclaredOnly lookup can throw AmbiguousMatchException or pick the wrong slot.
        private const BindingFlags WalkFlags = Flags | BindingFlags.DeclaredOnly;

        private static readonly Func<ParameterInfo, bool>[] NoParams = Array.Empty<Func<ParameterInfo, bool>>();
        private static readonly Func<ParameterInfo, bool>[] OneBool = { p => p.ParameterType == typeof(bool) };
        private static readonly Func<ParameterInfo, bool>[] OneEnum = { p => p.ParameterType.IsEnum };

        /// <summary>Resolve an instance method by name, matching only its <paramref name="leading"/> parameters and
        /// requiring every parameter past them to be optional.
        ///
        /// Vanilla routinely *appends* an optional parameter to an existing method - 0.19 did it to both
        /// Unit.AttachToBossUI(bool) and Npc.SetPhysicsEnabled(bool). A C# default value is a compile-time
        /// convenience that reflection does not see: an exact GetMethod(..., new[]{ typeof(bool) }, ...) stops
        /// matching the moment a second parameter appears, and Invoke with one argument throws. Matching a
        /// prefix instead makes every helper here immune to that whole class of change.
        ///
        /// Fewest parameters wins, so an unchanged exact-arity method still resolves exactly as it did before.
        /// Walks most-derived -> base with DeclaredOnly for the member-hiding reason above.</summary>
        private static bool TryResolve(object obj, string method, Func<ParameterInfo, bool>[] leading,
                                       out MethodInfo? resolved, Func<MethodInfo, bool>? accept = null)
        {
            resolved = null;
            for (Type? t = obj.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo? best = null;
                int bestCount = int.MaxValue;
                foreach (var cand in t.GetMethods(WalkFlags))
                {
                    if (cand.Name != method || cand.IsGenericMethodDefinition) continue;
                    if (accept != null && !accept(cand)) continue;
                    var ps = cand.GetParameters();
                    if (ps.Length < leading.Length || ps.Length >= bestCount) continue;

                    bool ok = true;
                    for (int i = 0; i < leading.Length && ok; i++)
                        ok = !ps[i].ParameterType.IsByRef && leading[i](ps[i]);
                    for (int i = leading.Length; i < ps.Length && ok; i++)
                        ok = ps[i].IsOptional;
                    if (!ok) continue;

                    best = cand;
                    bestCount = ps.Length;
                }
                if (best != null) { resolved = best; return true; }
            }
            return false;
        }

        /// <summary>Argument array for a method resolved by <see cref="TryResolve"/>: the caller's leading arguments
        /// plus the vanilla default of every trailing optional parameter the caller knows nothing about.</summary>
        private static object?[] BuildArgs(MethodInfo mi, params object?[] leadingArgs)
        {
            var ps = mi.GetParameters();
            if (ps.Length == leadingArgs.Length) return leadingArgs;
            var args = new object?[ps.Length];
            Array.Copy(leadingArgs, args, leadingArgs.Length);
            for (int i = leadingArgs.Length; i < ps.Length; i++)
            {
                var p = ps[i];
                args[i] = p.HasDefaultValue ? p.DefaultValue
                        : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                        : null;
            }
            return args;
        }

        public static object? GetMember(object? obj, string name)
        {
            if (obj == null) return null;
            try
            {
                for (Type? t = obj.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, WalkFlags);
                    if (f != null) return f.GetValue(obj);
                    var p = t.GetProperty(name, WalkFlags);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null);
                }
            }
            catch { }
            return null;
        }

        /// <summary>Write a field/property, most-derived first. False when the member is missing or read-only — callers
        /// decide whether that is fatal; nothing here throws.</summary>
        public static bool TrySetMember(object? obj, string name, object? value)
        {
            if (obj == null) return false;
            try
            {
                for (Type? t = obj.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, WalkFlags);
                    if (f != null) { f.SetValue(obj, value); return true; }
                    var p = t.GetProperty(name, WalkFlags);
                    if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0) { p.SetValue(obj, value, null); return true; }
                }
            }
            catch { }
            return false;
        }

        public static bool TryGetBool(object? obj, string name, out bool value)
        {
            value = false;
            var v = GetMember(obj, name);
            if (v is bool b) { value = b; return true; }
            return false;
        }

        public static bool TryGetInt(object? obj, string name, out int value)
        {
            value = 0;
            var v = GetMember(obj, name);
            try
            {
                switch (v)
                {
                    case int i: value = i; return true;
                    case short s: value = s; return true;
                    case byte by: value = by; return true;
                    case Enum e: value = Convert.ToInt32(e); return true;
                    default: return false;
                }
            }
            catch { return false; }
        }

        public static bool TryGetFloat(object? obj, string name, out float value)
        {
            value = 0f;
            var v = GetMember(obj, name);
            try
            {
                switch (v)
                {
                    case float f: value = f; return true;
                    case double d: value = (float)d; return true;
                    case int i: value = i; return true;
                    default: return false;
                }
            }
            catch { return false; }
        }

        /// <summary>True if the object's type (or a base type) declares an instance method by name that is callable
        /// with no arguments (parameterless, or every parameter optional). Several adapters use this as a type
        /// discriminator, so it stays a shape probe - a same-named method with a required parameter is not a match.</summary>
        public static bool HasMethod(object? obj, string method)
        {
            if (obj == null) return false;
            try { return TryResolve(obj, method, NoParams, out _); }
            catch { return false; }
        }

        /// <summary>Invoke a parameterless (or all-default) method by name. Returns true if invoked.</summary>
        public static bool TryInvoke(object? obj, string method, out string detail)
        {
            detail = "";
            if (obj == null) { detail = "null-instance"; return false; }
            try
            {
                if (!TryResolve(obj, method, NoParams, out var mi))
                { detail = $"method '{method}()' not found"; return false; }
                mi!.Invoke(obj, BuildArgs(mi));
                detail = $"invoked {obj.GetType().Name}.{method}()";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                detail = $"{method} threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                detail = $"{method} invoke failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>Call a parameterless instance method returning a float (e.g. Unit.GetCurrentHealth()).</summary>
        public static bool TryCallFloat(object? obj, string method, out float value)
        {
            value = 0f;
            if (obj == null) return false;
            try
            {
                if (TryResolve(obj, method, NoParams, out var mi,
                        m => m.ReturnType == typeof(float) || m.ReturnType == typeof(double)))
                {
                    value = Convert.ToSingle(mi!.Invoke(obj, BuildArgs(mi)));
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Invoke a single-argument instance method whose parameter is assignable from <paramref name="arg"/>
        /// (e.g. CousinHelper.SetNewPool(CousinPool), Unit.TeleportTo(Vector3)).</summary>
        public static bool TryInvokeArg(object? obj, string method, object arg, out string detail)
        {
            detail = "";
            if (obj == null || arg == null) { detail = "null"; return false; }
            try
            {
                var argType = arg.GetType();
                var leading = new Func<ParameterInfo, bool>[] { p => p.ParameterType.IsAssignableFrom(argType) };
                if (!TryResolve(obj, method, leading, out var mi))
                { detail = $"method '{method}({argType.Name}, ...)' not found"; return false; }
                mi!.Invoke(obj, BuildArgs(mi, arg));
                detail = $"invoked {obj.GetType().Name}.{method}({argType.Name})";
                return true;
            }
            catch (TargetInvocationException ex) { detail = $"{method} threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}"; return false; }
            catch (Exception ex) { detail = $"{method} failed: {ex.GetType().Name}"; return false; }
        }

        /// <summary>Fire a Unit's `onHealthChange` delegate with a normalized (0..1) health so the boss bar (which
        /// subscribes to it on Attach) updates. Stats.SetStatus(...,false) writes the value without raising the
        /// status-changed event, so the bar would otherwise stay stale on the Client.</summary>
        public static bool TryFireHealthChange(object? unit, float normalized)
        {
            if (unit == null) return false;
            try
            {
                var f = GetMemberField(unit, "onHealthChange");
                if (f?.GetValue(unit) is Delegate del) { del.DynamicInvoke(normalized); return true; }
            }
            catch { }
            return false;
        }

        private static FieldInfo? GetMemberField(object obj, string name)
        {
            for (Type? t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, WalkFlags);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Invoke an instance method whose first parameter is a bool (e.g. Unit.AttachToBossUI(bool)).
        /// Trailing optional parameters keep their vanilla defaults - see <see cref="TryResolve"/>.</summary>
        public static bool TryInvokeBool(object? obj, string method, bool arg, out string detail)
        {
            detail = "";
            if (obj == null) { detail = "null-instance"; return false; }
            try
            {
                if (!TryResolve(obj, method, OneBool, out var mi))
                { detail = $"method '{method}(bool, ...)' not found"; return false; }
                mi!.Invoke(obj, BuildArgs(mi, arg));
                detail = $"invoked {obj.GetType().Name}.{method}({arg})";
                return true;
            }
            catch (TargetInvocationException ex) { detail = $"{method} threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}"; return false; }
            catch (Exception ex) { detail = $"{method} invoke failed: {ex.GetType().Name}"; return false; }
        }

        /// <summary>Invoke an instance method that takes a single enum argument, passing <paramref name="enumInt"/>
        /// converted to that enum type. Used to drive WitchBossController.ChangePhase(WitchPhase).</summary>
        public static bool TryInvokeWithEnumInt(object? obj, string method, int enumInt, out string detail)
        {
            detail = "";
            if (obj == null) { detail = "null-instance"; return false; }
            try
            {
                if (!TryResolve(obj, method, OneEnum, out var mi))
                { detail = $"enum-method '{method}' not found"; return false; }
                Type enumType = mi!.GetParameters()[0].ParameterType;
                object arg = Enum.ToObject(enumType, enumInt);
                mi.Invoke(obj, BuildArgs(mi, arg));
                detail = $"invoked {obj.GetType().Name}.{method}({enumType.Name}={arg})";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                detail = $"{method} threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (Exception ex) { detail = $"{method} invoke failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        public static bool TryGetPosition(object? component, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                if (component is Component c && c != null) { pos = c.transform.position; return true; }
                if (component is GameObject go && go != null) { pos = go.transform.position; return true; }
            }
            catch { }
            return false;
        }

        public static string GameObjectPath(object? component)
        {
            try
            {
                Transform? tr = (component is Component c && c != null) ? c.transform
                    : (component is GameObject go && go != null ? go.transform : null);
                if (tr == null) return "<no-transform>";
                var parts = new System.Collections.Generic.List<string>();
                for (Transform? t = tr; t != null && parts.Count < 10; t = t.parent) parts.Add(t.name);
                parts.Reverse();
                return string.Join("/", parts);
            }
            catch { return "<err>"; }
        }

        public static string RootName(object? component)
        {
            try
            {
                if (component is Component c && c != null && c.gameObject != null) return c.gameObject.name;
                if (component is GameObject go && go != null) return go.name;
            }
            catch { }
            return component?.GetType().Name ?? "<unknown>";
        }

        /// <summary>Best-effort stable unit identifier of a spawned Unit (UnitIdentifier/UnitId + AsGlobalId), used by
        /// the boss dynamic-spawn manifest for diagnostics. Empty when none is readable.</summary>
        public static string ReadUnitId(object? unit)
        {
            try
            {
                var idObj = GetMember(unit, "UnitIdentifier") ?? GetMember(unit, "unitIdentifier")
                          ?? GetMember(unit, "UnitId") ?? GetMember(unit, "unitId");
                if (idObj == null) return "";
                // Prefer a global id if the identifier exposes one.
                try
                {
                    var m = idObj.GetType().GetMethod("AsGlobalId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    var g = m?.Invoke(idObj, null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(g) && g != "null") return g!.Length > 64 ? g.Substring(0, 64) : g;
                }
                catch { }
                var s = idObj.ToString();
                if (string.IsNullOrWhiteSpace(s) || s == "null") return "";
                return s!.Length > 64 ? s.Substring(0, 64) : s;
            }
            catch { return ""; }
        }

        public static int InstanceId(object? component)
        {
            try { if (component is UnityEngine.Object uo && uo != null) return uo.GetInstanceID(); }
            catch { }
            return 0;
        }

        /// <summary>Resolve a type by full names first, then by an assembly scan on the short name. Null if absent.</summary>
        public static Type? FindType(string shortName, params string[] fullNames)
        {
            foreach (var fn in fullNames)
            {
                try { var t = AccessTools.TypeByName(fn); if (t != null) return t; } catch { }
            }
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    foreach (var t in types)
                        if (t != null && t.Name == shortName) return t;
                }
            }
            catch { }
            return null;
        }
    }
}
