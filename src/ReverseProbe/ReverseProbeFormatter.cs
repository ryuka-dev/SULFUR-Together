using System;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.ReverseProbe
{
    public static class ReverseProbeFormatter
    {
        public static string FormatInstance(object? obj)
        {
            if (obj == null) return "<null>";
            string typeName = obj.GetType().Name;
            string name     = TryGetString(obj, "name") ?? TryGetString(obj, "Name") ?? "?";
            string id       = GetInstanceId(obj);
            return $"{typeName}(name={name}, id={id})";
        }

        /// <summary>
        /// Best-effort item display name extraction for compact probe summaries.
        /// Stores/returns strings only; no Unity object references are retained.
        /// </summary>
        public static string FormatItemName(object? obj)
        {
            if (obj == null) return "<unknown>";

            try
            {
                if (obj is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();

                string? explicitName =
                    TryGetString(obj, "DisplayName") ??
                    TryGetString(obj, "displayName") ??
                    TryGetString(obj, "ItemName") ??
                    TryGetString(obj, "itemName") ??
                    TryGetString(obj, "Title") ??
                    TryGetString(obj, "title");

                explicitName = CleanItemName(explicitName);
                if (explicitName != null) return explicitName;

                string? toStringName = CleanItemName(obj.ToString());
                string typeName = obj.GetType().FullName ?? obj.GetType().Name;
                if (toStringName != null && toStringName != typeName && !toStringName.Contains("(") && !toStringName.Contains("PerfectRandom."))
                    return toStringName;

                string? unityName = CleanItemName(TryGetString(obj, "name") ?? TryGetString(obj, "Name"));
                if (unityName != null) return unityName;
            }
            catch { }

            return "<unknown>";
        }

        public static string GetInstanceId(object? obj)
        {
            if (obj == null) return "null";
            return TryCallMethod(obj, "GetInstanceID") ?? obj.GetHashCode().ToString();
        }

        public static string FormatVector3(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
        public static string FormatQuat(Quaternion q)  => $"euler({q.eulerAngles.x:F1},{q.eulerAngles.y:F1},{q.eulerAngles.z:F1})";

        private static string? TryGetString(object obj, string memberName)
        {
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = obj.GetType().GetProperty(memberName, bf);
                if (prop != null) return prop.GetValue(obj)?.ToString();
                var field = obj.GetType().GetField(memberName, bf);
                if (field != null) return field.GetValue(obj)?.ToString();
            }
            catch { }
            return null;
        }

        private static string? TryCallMethod(object obj, string methodName)
        {
            try
            {
                var m = obj.GetType().GetMethod(methodName, Type.EmptyTypes);
                return m?.Invoke(obj, null)?.ToString();
            }
            catch { return null; }
        }

        private static string? CleanItemName(string? raw)
        {
            if (raw == null) return null;
            string value = raw.Trim();
            if (value.Length == 0) return null;
            if (value == "?" || value == "<null>" || value == "null") return null;
            if (value.Contains("UnityEngine.") || value.Contains("System.")) return null;

            if (value == "Currency_SulfCoin") return "Cash Money Coin";
            if (value == "Currency_SulfTwenny") return "Cash Money Twenny";

            return value;
        }
    }
}
