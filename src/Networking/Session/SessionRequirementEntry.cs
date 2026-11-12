using System;
using System.Globalization;
using System.Text;

namespace SULFURTogether.Networking.Session
{
    /// <summary>An inclusive id range a session requirement claims for one id kind. <see cref="Kind"/> is the wire
    /// byte: a kind this build does not know still takes part in comparing two entries, and in nothing else.</summary>
    internal readonly struct SessionIdRange : IEquatable<SessionIdRange>
    {
        public const byte KindItem = 1;

        public SessionIdRange(byte kind, ushort min, ushort max)
        {
            Kind = kind;
            Min = min;
            Max = max;
        }

        public byte   Kind { get; }
        public ushort Min  { get; }
        public ushort Max  { get; }

        public bool Equals(SessionIdRange other) => Kind == other.Kind && Min == other.Min && Max == other.Max;
        public override bool Equals(object? obj) => obj is SessionIdRange other && Equals(other);
        public override int GetHashCode() => (Kind << 24) ^ (Min << 12) ^ Max;
        public override string ToString() => $"{Kind}:{Min}-{Max}";
    }

    /// <summary>
    /// One session requirement as it is exchanged in the handshake: what a peer has installed that the other end must
    /// have in the same state for ids in <see cref="Claims"/> to mean the same thing on both machines.
    /// </summary>
    internal sealed class SessionRequirementEntry
    {
        public SessionRequirementEntry(string id, string displayName, string? digest, SessionIdRange[] claims)
        {
            Id = id;
            DisplayName = displayName;
            Digest = digest;
            Claims = claims;
        }

        /// <summary>The registering mod's own id. Restricted to <see cref="SessionRequirementLimits.IsValidId"/>.</summary>
        public string Id { get; }

        /// <summary>Text for the player, always passed through <see cref="SessionRequirementLimits.SanitizeDisplayName"/>,
        /// whether it was registered here or sent by a peer. It never takes part in a comparison. Two entries can carry
        /// the same name, so anything that lists entries shows the id beside it.</summary>
        public string DisplayName { get; }

        /// <summary>The state fingerprint, or null when its owner could not produce one. A null digest equals nothing,
        /// including another null: two ends that both failed to describe their content have not shown it to match.</summary>
        public string? Digest { get; }

        public SessionIdRange[] Claims { get; }
    }

    /// <summary>The bounds every requirement obeys, whether it was registered here or arrived from a peer.</summary>
    internal static class SessionRequirementLimits
    {
        public const int MaxEntries          = 64;
        public const int MaxIdLength         = 64;
        public const int MaxDisplayNameChars = 48;
        public const int MaxDisplayNameBytes = MaxDisplayNameChars * 3; // UTF-8 of 48 UTF-16 code units is at most 144 bytes
        public const int MaxDigestLength     = 64;
        public const int MaxClaimsPerEntry   = 8;

        /// <summary>1 to 64 characters of <c>[A-Za-z0-9._-]</c>.</summary>
        public static bool IsValidId(string? id)
        {
            if (id == null || id.Length == 0 || id.Length > MaxIdLength) return false;
            foreach (char c in id)
                if (!IsIdChar(c)) return false;
            return true;
        }

        public static bool IsIdChar(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';

        /// <summary>1 to 64 printable ASCII characters without a space, so equal digests are equal byte for byte.</summary>
        public static bool IsValidDigest(string? digest)
        {
            if (digest == null || digest.Length == 0 || digest.Length > MaxDigestLength) return false;
            foreach (char c in digest)
                if (c < 0x21 || c > 0x7E) return false;
            return true;
        }

        /// <summary>
        /// Make a name safe to put in a text row or a toast. What is left is at most <see cref="MaxDisplayNameChars"/>
        /// characters of the Basic Multilingual Plane with:
        /// no control, format, private-use or unassigned characters (line breaks, bidirectional controls, zero-width
        /// characters); no characters that only look like a blank; every run of white space as one space; at most two
        /// combining marks in a row; and none of the characters the text renderer or the message formatter treats as
        /// syntax: rich-text brackets, the backslash that starts an escape such as <c>\n</c> or <c><</c>, and
        /// placeholder braces. Characters outside the plane (surrogate pairs) are dropped.
        /// False when nothing visible is left.
        /// </summary>
        public static bool TrySanitizeDisplayName(string? name, out string cleaned)
        {
            cleaned = "";
            if (string.IsNullOrEmpty(name)) return false;

            var sb = new StringBuilder(Math.Min(name!.Length, MaxDisplayNameChars));
            int marks = 0;
            bool pendingSpace = false;
            bool visible = false;
            foreach (char raw in name)
            {
                char c = raw;
                if (c == '<' || c == '{') c = '(';
                else if (c == '>' || c == '}') c = ')';
                else if (c == '\\') c = '/';

                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; marks = 0; continue; }
                if (LooksBlank(c)) continue;

                bool mark = false;
                switch (char.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                        continue;
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.SpacingCombiningMark:
                    case UnicodeCategory.EnclosingMark:
                        mark = true;
                        break;
                }

                if (mark)
                {
                    if (!visible || pendingSpace || marks >= 2) continue; // a mark needs a base, and two are enough
                    marks++;
                }
                else marks = 0;

                int needed = pendingSpace ? 2 : 1;
                if (sb.Length + needed > MaxDisplayNameChars) break;
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
                if (!mark) visible = true;
            }

            if (!visible) return false;
            cleaned = sb.ToString();
            return true;
        }

        /// <summary><see cref="TrySanitizeDisplayName"/>, with <paramref name="fallback"/> (an entry id, whose
        /// character set is already safe) cut to the same length when nothing visible is left.</summary>
        public static string SanitizeDisplayName(string? name, string fallback)
        {
            if (TrySanitizeDisplayName(name, out string cleaned)) return cleaned;
            return fallback.Length <= MaxDisplayNameChars ? fallback : fallback.Substring(0, MaxDisplayNameChars);
        }

        // Letters and symbols that render as empty space although Unicode does not class them as white space or format.
        private static bool LooksBlank(char c)
            => c == 0x115F || c == 0x1160 || c == 0x3164 || c == 0xFFA0 || c == 0x2800 || c == 0x180E || c == 0xFFFC;
    }
}
