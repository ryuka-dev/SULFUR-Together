using System;
using System.Reflection;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// FF-1: classifies a <c>Unit.ReceiveDamage</c> source argument as "damage dealt by the LOCAL player" or not.
    ///
    /// The two native overloads carry the source at parameter index 2 as either an <c>IDamager</c> (typically the
    /// player's own <c>Unit</c>) or a <c>DamageSourceData</c> (fields verified by DLL reverse — see
    /// NetGameplayProbeManager: <c>isPlayer</c>, <c>sourceUnit</c>, <c>sourceWeapon</c>, <c>projectile</c>,
    /// <c>melee</c>, …). Remote players' replayed shots are zero-damage visuals (PlayerWeaponFireManager), so on any
    /// end the only real player-sourced damage is the local player's — which makes the <c>isPlayer</c> flag a safe
    /// final fallback.
    ///
    /// Callers choose the failure policy: the host proxy-forward branch treats "not classified as player" as enemy
    /// damage (fail-open, never blocks real enemy damage); the client hit branch only acts on a positive result
    /// (fail-closed, never fabricates a hit request).
    /// </summary>
    internal static class FriendlyFireClassifier
    {
        private static Type? _sourceDataType;     // concrete DamageSourceData type, learned from the first instance
        private static FieldInfo? _sourceUnitField;
        private static FieldInfo? _isPlayerField;

        /// <summary>True only when <paramref name="rawSource"/> is positively identified as the local player.</summary>
        public static bool IsFromLocalPlayer(object? rawSource)
        {
            if (rawSource == null) return false;
            try
            {
                object? localPlayerUnit = Boss.BossDamageReflect.ResolveHostPlayerUnit();

                // IDamager overload: the source IS the damaging Unit (player weapon hits pass PlayerUnit directly).
                if (localPlayerUnit != null && ReferenceEquals(rawSource, localPlayerUnit)) return true;

                // DamageSourceData overload: pull the fields.
                ResolveSourceDataFields(rawSource);
                if (_sourceDataType == null || !_sourceDataType.IsInstanceOfType(rawSource)) return false;

                if (_sourceUnitField != null && localPlayerUnit != null
                    && ReferenceEquals(_sourceUnitField.GetValue(rawSource), localPlayerUnit)) return true;

                if (_isPlayerField != null && _isPlayerField.GetValue(rawSource) is bool isPlayer && isPlayer)
                    return true;

                return false;
            }
            catch { return false; }
        }

        private static void ResolveSourceDataFields(object rawSource)
        {
            if (_sourceDataType != null) return;
            var t = rawSource.GetType();
            if (t.Name != "DamageSourceData") return; // the IDamager overload passes a Unit — don't lock onto it
            _sourceDataType   = t;
            _sourceUnitField  = HarmonyLib.AccessTools.Field(t, "sourceUnit");
            _isPlayerField    = HarmonyLib.AccessTools.Field(t, "isPlayer");
        }

        // ---- gated sampling diagnostics (LogFriendlyFire): the in-game probe for melee/explosion coverage ----

        private const int MaxSampleLines = 20;
        private static int _sampleLines;

        /// <summary>One gated, budgeted log line per classified proxy hit — enough to settle which native damage
        /// sources (bullets / melee / explosions) the classifier covers, without per-hit log I/O in release.</summary>
        public static void LogClassification(string context, object? rawSource, float damage, bool fromLocalPlayer)
        {
            try
            {
                if (!Plugin.Cfg.LogFriendlyFire.Value) return;
                if (_sampleLines >= MaxSampleLines) return;
                _sampleLines++;
                string desc = Describe(rawSource);
                Plugin.Log.Info($"[FF] {context}: fromLocalPlayer={fromLocalPlayer} dmg={damage:F1} source={desc} ({_sampleLines}/{MaxSampleLines})");
            }
            catch { }
        }

        private static string Describe(object? rawSource)
        {
            if (rawSource == null) return "null";
            try
            {
                var t = rawSource.GetType();
                if (_sourceDataType != null && _sourceDataType.IsInstanceOfType(rawSource))
                {
                    string ip = _isPlayerField?.GetValue(rawSource)?.ToString() ?? "?";
                    string su = _sourceUnitField?.GetValue(rawSource) is UnityEngine.Object u && u != null ? u.name : "null";
                    string melee = HarmonyLib.AccessTools.Field(t, "melee")?.GetValue(rawSource)?.ToString() ?? "?";
                    string proj  = HarmonyLib.AccessTools.Field(t, "projectile")?.GetValue(rawSource) == null ? "null" : "set";
                    return $"{t.Name}(isPlayer={ip},sourceUnit={su},melee={melee},projectile={proj})";
                }
                return t.Name;
            }
            catch { return rawSource.GetType().Name; }
        }
    }
}
