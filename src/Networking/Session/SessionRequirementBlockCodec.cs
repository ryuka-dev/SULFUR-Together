using System;
using System.Collections.Generic;
using System.Text;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// The requirement block of the handshake as plain bytes. It knows nothing about the transport: the handshake
    /// carries the result as one length-prefixed byte array.
    ///
    /// <code>
    /// byte   version (1)
    /// byte   entry count                      (at most 64)
    /// entry: byte idLength, id                (ASCII, [A-Za-z0-9._-], 1 to 64)
    ///        byte nameLength, name            (UTF-8, at most 144 bytes and 48 characters)
    ///        byte hasDigest (0 or 1)
    ///        [byte digestLength, digest]      (printable ASCII, 1 to 64; only when hasDigest is 1)
    ///        byte claim count                 (at most 8)
    ///        claim: byte kind, ushort min, ushort max   (little endian, 1 &lt;= min &lt;= max)
    /// </code>
    ///
    /// Decoding is all or nothing. Anything over a bound, a repeated id, an invalid string or bytes left over makes
    /// the whole block invalid; every length is checked against what is left before it is read.
    /// </summary>
    internal static class SessionRequirementBlockCodec
    {
        public const byte Version = 1;

        // Throws on a malformed sequence instead of substituting U+FFFD, which would let two different byte strings
        // decode to one name.
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(IReadOnlyList<SessionRequirementEntry> entries)
        {
            int count = Math.Min(entries.Count, SessionRequirementLimits.MaxEntries);
            var buf = new List<byte>(2 + count * 48) { Version, (byte)count };
            for (int i = 0; i < count; i++)
            {
                SessionRequirementEntry e = entries[i];

                PutAscii(buf, e.Id);

                byte[] name = EncodeName(e.DisplayName);
                buf.Add((byte)name.Length);
                buf.AddRange(name);

                if (e.Digest == null) buf.Add(0);
                else { buf.Add(1); PutAscii(buf, e.Digest); }

                int claims = Math.Min(e.Claims.Length, SessionRequirementLimits.MaxClaimsPerEntry);
                buf.Add((byte)claims);
                for (int c = 0; c < claims; c++)
                {
                    SessionIdRange r = e.Claims[c];
                    buf.Add(r.Kind);
                    buf.Add((byte)(r.Min & 0xFF));
                    buf.Add((byte)(r.Min >> 8));
                    buf.Add((byte)(r.Max & 0xFF));
                    buf.Add((byte)(r.Max >> 8));
                }
            }
            return buf.ToArray();
        }

        public static bool TryDecode(byte[]? data, out SessionRequirementEntry[] entries)
        {
            entries = Array.Empty<SessionRequirementEntry>();
            if (data == null || data.Length < 2) return false;

            int pos = 0;
            if (data[pos++] != Version) return false;
            int count = data[pos++];
            if (count > SessionRequirementLimits.MaxEntries) return false;

            var result = new SessionRequirementEntry[count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                if (!TryGetAscii(data, ref pos, SessionRequirementLimits.MaxIdLength, out string id)) return false;
                if (!SessionRequirementLimits.IsValidId(id)) return false;
                if (!seen.Add(id)) return false;

                if (!TryGetName(data, ref pos, out string rawName)) return false;

                if (pos >= data.Length) return false;
                byte hasDigest = data[pos++];
                string? digest = null;
                if (hasDigest == 1)
                {
                    if (!TryGetAscii(data, ref pos, SessionRequirementLimits.MaxDigestLength, out string d)) return false;
                    if (!SessionRequirementLimits.IsValidDigest(d)) return false;
                    digest = d;
                }
                else if (hasDigest != 0) return false;

                if (pos >= data.Length) return false;
                int claimCount = data[pos++];
                if (claimCount > SessionRequirementLimits.MaxClaimsPerEntry) return false;
                if (data.Length - pos < claimCount * 5) return false;
                var claims = new SessionIdRange[claimCount];
                for (int c = 0; c < claimCount; c++)
                {
                    byte kind = data[pos];
                    ushort min = (ushort)(data[pos + 1] | (data[pos + 2] << 8));
                    ushort max = (ushort)(data[pos + 3] | (data[pos + 4] << 8));
                    pos += 5;
                    if (min == 0 || min > max) return false;
                    claims[c] = new SessionIdRange(kind, min, max);
                }

                result[i] = new SessionRequirementEntry(id, SessionRequirementLimits.SanitizeDisplayName(rawName, id), digest, claims);
            }

            if (pos != data.Length) return false; // nothing may follow the last entry
            entries = result;
            return true;
        }

        private static void PutAscii(List<byte> buf, string s)
        {
            buf.Add((byte)s.Length);
            foreach (char c in s) buf.Add((byte)c);
        }

        // The name is display text only; one too long for the wire is cut at a character boundary rather than refused.
        private static byte[] EncodeName(string name)
        {
            string s = name ?? "";
            if (s.Length > SessionRequirementLimits.MaxDisplayNameChars)
                s = s.Substring(0, SessionRequirementLimits.MaxDisplayNameChars);
            while (true)
            {
                try
                {
                    byte[] bytes = StrictUtf8.GetBytes(s);
                    if (bytes.Length <= SessionRequirementLimits.MaxDisplayNameBytes) return bytes;
                }
                catch (ArgumentException) { /* a lone surrogate, possibly left by the cut above: drop a character and retry */ }
                if (s.Length == 0) return Array.Empty<byte>();
                s = s.Substring(0, s.Length - 1);
            }
        }

        private static bool TryGetAscii(byte[] data, ref int pos, int maxLength, out string value)
        {
            value = "";
            if (pos >= data.Length) return false;
            int len = data[pos++];
            if (len == 0 || len > maxLength || data.Length - pos < len) return false;
            var chars = new char[len];
            for (int i = 0; i < len; i++)
            {
                byte b = data[pos + i];
                if (b > 0x7E) return false;
                chars[i] = (char)b;
            }
            pos += len;
            value = new string(chars);
            return true;
        }

        private static bool TryGetName(byte[] data, ref int pos, out string value)
        {
            value = "";
            if (pos >= data.Length) return false;
            int len = data[pos++];
            if (len > SessionRequirementLimits.MaxDisplayNameBytes || data.Length - pos < len) return false;
            try { value = StrictUtf8.GetString(data, pos, len); }
            catch (ArgumentException) { return false; }
            pos += len;
            return value.Length <= SessionRequirementLimits.MaxDisplayNameChars;
        }
    }
}
