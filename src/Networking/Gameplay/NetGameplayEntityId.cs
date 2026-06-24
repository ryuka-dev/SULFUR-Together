using System;
using System.Reflection;
using UnityEngine;
using SULFURTogether.ReverseProbe;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.0.0-A local-only entity identity candidate.
    ///
    /// This is not a network id yet. It is a structured probe record used to compare
    /// Host/Client logs under the same chapter/level/seed and decide which fields are
    /// stable enough to become a future NetworkEntityId.
    /// </summary>
    internal sealed class NetGameplayEntityId
    {
        public string LocalInstanceId { get; private set; } = "null";
        public int UnityInstanceId { get; private set; }
        public string TypeName { get; private set; } = "<unknown>";
        public string FullTypeName { get; private set; } = "<unknown>";
        public string UnitIdentifier { get; private set; } = "";
        public string UnitGlobalId { get; private set; } = "";
        public string CandidateKey { get; private set; } = "";

        public static NetGameplayEntityId FromObject(object? obj)
        {
            var id = new NetGameplayEntityId();
            if (obj == null)
            {
                id.CandidateKey = "null";
                return id;
            }

            Type type = obj.GetType();
            id.TypeName = type.Name;
            id.FullTypeName = type.FullName ?? type.Name;
            id.LocalInstanceId = ReverseProbeFormatter.GetInstanceId(obj);

            if (obj is UnityEngine.Object unityObject)
                id.UnityInstanceId = unityObject.GetInstanceID();
            else if (int.TryParse(id.LocalInstanceId, out var parsed))
                id.UnityInstanceId = parsed;

            object? unitIdentifier = TryGetMemberValue(obj, "UnitIdentifier")
                ?? TryGetMemberValue(obj, "unitIdentifier")
                ?? TryGetMemberValue(obj, "UnitId")
                ?? TryGetMemberValue(obj, "unitId")
                ?? TryGetMemberValue(obj, "Id")
                ?? TryGetMemberValue(obj, "id");

            id.UnitIdentifier = CleanValue(unitIdentifier?.ToString());
            id.UnitGlobalId = CleanValue(TryGetGlobalId(unitIdentifier));

            string stablePart = !string.IsNullOrWhiteSpace(id.UnitGlobalId)
                ? "global=" + id.UnitGlobalId
                : !string.IsNullOrWhiteSpace(id.UnitIdentifier)
                    ? "unit=" + id.UnitIdentifier
                    : "local=" + id.LocalInstanceId;

            id.CandidateKey = $"{id.TypeName}|{stablePart}";
            return id;
        }

        public string FormatCompact()
        {
            string unit = string.IsNullOrWhiteSpace(UnitIdentifier) ? "-" : UnitIdentifier;
            string global = string.IsNullOrWhiteSpace(UnitGlobalId) ? "-" : UnitGlobalId;
            return $"type={TypeName} local={LocalInstanceId} unity={UnityInstanceId} unitId={unit} globalId={global}";
        }

        private static object? TryGetMemberValue(object obj, string memberName)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Type type = obj.GetType();

                var prop = type.GetProperty(memberName, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(obj, null);

                var field = type.GetField(memberName, flags);
                if (field != null)
                    return field.GetValue(obj);
            }
            catch { }

            return null;
        }

        private static string TryGetGlobalId(object? unitIdentifier)
        {
            if (unitIdentifier == null) return "";

            try
            {
                var method = unitIdentifier.GetType().GetMethod("AsGlobalId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                object? result = method?.Invoke(unitIdentifier, null);
                string value = CleanValue(result?.ToString());
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }

            object? global = TryGetMemberValue(unitIdentifier, "GlobalId")
                ?? TryGetMemberValue(unitIdentifier, "globalId");
            return CleanValue(global?.ToString());
        }

        private static string CleanValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string text = value.Trim();
            if (text == "null" || text == "<null>" || text == "?") return "";
            if (text.Length > 96) text = text.Substring(0, 96);
            return text;
        }
    }
}
