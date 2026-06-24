using System;
using System.Collections.Generic;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Centralized scene name normalization used only for network metadata comparisons
    /// and manual-follow lookup. It does not change the original logged scene name.
    /// </summary>
    internal static class NetSceneName
    {
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The game reports this transition environment while the loadable id is Act_01_Hedgemaze.
            { "Act_01_HedgemazeFromChurch", "Act_01_Hedgemaze" },
            { "HedgemazeFromChurch", "Act_01_Hedgemaze" },
        };

        public static string Clean(string? value)
        {
            if (value == null) return "<unknown>";
            string clean = value.Trim();
            return clean.Length == 0 ? "<unknown>" : clean;
        }

        public static string Canonicalize(string? value)
        {
            string clean = Clean(value);
            if (clean == "<unknown>") return clean;

            if (Aliases.TryGetValue(clean, out var mapped))
                return mapped;

            if (clean.EndsWith("FromChurch", StringComparison.OrdinalIgnoreCase))
            {
                string withoutSuffix = clean.Substring(0, clean.Length - "FromChurch".Length);
                if (!string.IsNullOrWhiteSpace(withoutSuffix))
                    return withoutSuffix;
            }

            return clean;
        }

        public static bool SameScene(string? leftChapter, int leftLevel, string? rightChapter, int rightLevel)
        {
            return leftLevel >= 0
                && rightLevel >= 0
                && leftLevel == rightLevel
                && string.Equals(Canonicalize(leftChapter), Canonicalize(rightChapter), StringComparison.OrdinalIgnoreCase);
        }

        public static string SceneKey(string? chapterName, int levelIndex)
        {
            return $"{Clean(chapterName)}:{levelIndex}";
        }

        public static string SceneCompareKey(string? chapterName, int levelIndex)
        {
            return $"{Canonicalize(chapterName)}:{levelIndex}";
        }

        public static IEnumerable<string> LookupCandidates(string? value)
        {
            string clean = Clean(value);
            if (clean == "<unknown>") yield break;

            yield return clean;

            string canonical = Canonicalize(clean);
            if (!string.Equals(canonical, clean, StringComparison.OrdinalIgnoreCase))
                yield return canonical;

            // A small extra fallback for game ids that may omit common Act prefixes in assets.
            if (canonical.StartsWith("Act_", StringComparison.OrdinalIgnoreCase))
            {
                int idx = canonical.IndexOf('_', 4);
                if (idx > 0 && idx + 1 < canonical.Length)
                    yield return canonical.Substring(idx + 1);
            }
        }
    }
}
