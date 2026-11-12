using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SULFURTogether.Networking.Session
{
    internal enum SessionDifferenceKind
    {
        /// <summary>This machine has the entry and the peer does not.</summary>
        OnlyHere,
        /// <summary>The peer has the entry and this machine does not.</summary>
        OnlyThere,
        /// <summary>Both have it, in different states.</summary>
        Different,
    }

    /// <summary>One requirement the two ends do not share in the same state.</summary>
    internal sealed class SessionDifference
    {
        public SessionDifference(string id, string displayName, SessionDifferenceKind kind)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
        }

        public string Id { get; }

        /// <summary>This machine's own name for the entry when it has one, otherwise the peer's, sanitised.</summary>
        public string DisplayName { get; }

        public SessionDifferenceKind Kind { get; }
    }

    /// <summary>
    /// The result of comparing what this machine and one peer presented in the handshake. Pure: it reads two lists
    /// and holds what follows from them, the differences to show a player and the item ids that must not cross.
    /// </summary>
    internal sealed class SessionContentComparison
    {
        private SessionContentComparison(SessionDifference[] differences, SessionIdRange[] withheld, string fingerprint)
        {
            Differences = differences;
            WithheldItemRanges = withheld;
            Fingerprint = fingerprint;
        }

        public bool IsMatch => Differences.Length == 0;

        /// <summary>In ordinal id order.</summary>
        public SessionDifference[] Differences { get; }

        /// <summary>The item ids that mean different things on the two machines: the item claims of every differing
        /// entry, from both sides, cut to the ids above the game's own and merged.</summary>
        public SessionIdRange[] WithheldItemRanges { get; }

        /// <summary>Identifies this exact set of differences: the same text if and only if the same entries differ in
        /// the same way. Empty when there is no difference.</summary>
        public string Fingerprint { get; }

        public IPeerItemIdPolicy CreatePolicy()
            => IsMatch ? (IPeerItemIdPolicy)MatchedItemIdPolicy.Instance : new WithheldRangesItemIdPolicy(WithheldItemRanges);

        public static SessionContentComparison Compare(
            IReadOnlyList<SessionRequirementEntry> here, IReadOnlyList<SessionRequirementEntry> there, int vanillaCeiling)
        {
            var hereById = new SortedDictionary<string, SessionRequirementEntry>(StringComparer.Ordinal);
            var thereById = new Dictionary<string, SessionRequirementEntry>(StringComparer.Ordinal);
            foreach (SessionRequirementEntry e in here) hereById[e.Id] = e;
            foreach (SessionRequirementEntry e in there) thereById[e.Id] = e;

            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string id in hereById.Keys) ids.Add(id);
            foreach (string id in thereById.Keys) ids.Add(id);

            var differences = new List<SessionDifference>();
            var ranges = new List<SessionIdRange>();
            var fingerprint = new StringBuilder();

            foreach (string id in ids)
            {
                hereById.TryGetValue(id, out SessionRequirementEntry? mine);
                thereById.TryGetValue(id, out SessionRequirementEntry? theirs);
                if (mine != null && theirs != null && SameState(mine, theirs)) continue;

                SessionDifferenceKind kind = mine == null ? SessionDifferenceKind.OnlyThere
                                           : theirs == null ? SessionDifferenceKind.OnlyHere
                                           : SessionDifferenceKind.Different;
                differences.Add(new SessionDifference(id, (mine ?? theirs)!.DisplayName, kind));

                if (mine != null) AddItemRanges(ranges, mine.Claims, vanillaCeiling);
                if (theirs != null) AddItemRanges(ranges, theirs.Claims, vanillaCeiling);

                // '\n' and ' ' cannot occur in an id, a digest or a claim, so the parts cannot run into each other.
                fingerprint.Append(id).Append(' ');
                AppendState(fingerprint, mine);
                fingerprint.Append(' ');
                AppendState(fingerprint, theirs);
                fingerprint.Append('\n');
            }

            return new SessionContentComparison(differences.ToArray(), Merge(ranges), fingerprint.ToString());
        }

        // Equal digests and equal claims. An entry without a digest is in the same state as nothing.
        private static bool SameState(SessionRequirementEntry a, SessionRequirementEntry b)
        {
            if (a.Digest == null || b.Digest == null) return false;
            if (!string.Equals(a.Digest, b.Digest, StringComparison.Ordinal)) return false;
            return string.Equals(CanonicalClaims(a.Claims), CanonicalClaims(b.Claims), StringComparison.Ordinal);
        }

        // The claims exactly as declared (every kind, nothing cut), ordered and without repeats.
        private static string CanonicalClaims(SessionIdRange[] claims)
        {
            var parts = new SortedSet<string>(StringComparer.Ordinal);
            foreach (SessionIdRange c in claims)
                parts.Add(c.Kind.ToString("D3", CultureInfo.InvariantCulture) + ":" + c.Min.ToString("D5", CultureInfo.InvariantCulture)
                          + "-" + c.Max.ToString("D5", CultureInfo.InvariantCulture));
            return string.Join(",", parts);
        }

        private static void AppendState(StringBuilder sb, SessionRequirementEntry? e)
        {
            if (e == null) { sb.Append("absent"); return; }
            sb.Append(e.Digest == null ? "nodigest" : "digest=" + e.Digest).Append('[').Append(CanonicalClaims(e.Claims)).Append(']');
        }

        private static void AddItemRanges(List<SessionIdRange> into, SessionIdRange[] claims, int vanillaCeiling)
        {
            foreach (SessionIdRange c in claims)
            {
                if (c.Kind != SessionIdRange.KindItem) continue;
                int min = Math.Max(c.Min, vanillaCeiling + 1);
                if (min > c.Max) continue; // entirely inside the game's own ids: not honoured
                into.Add(new SessionIdRange(SessionIdRange.KindItem, (ushort)min, c.Max));
            }
        }

        private static SessionIdRange[] Merge(List<SessionIdRange> ranges)
        {
            if (ranges.Count == 0) return Array.Empty<SessionIdRange>();
            ranges.Sort((a, b) => a.Min != b.Min ? a.Min.CompareTo(b.Min) : a.Max.CompareTo(b.Max));

            var merged = new List<SessionIdRange>();
            int min = ranges[0].Min, max = ranges[0].Max;
            for (int i = 1; i < ranges.Count; i++)
            {
                if (ranges[i].Min <= max + 1) { if (ranges[i].Max > max) max = ranges[i].Max; continue; }
                merged.Add(new SessionIdRange(SessionIdRange.KindItem, (ushort)min, (ushort)max));
                min = ranges[i].Min;
                max = ranges[i].Max;
            }
            merged.Add(new SessionIdRange(SessionIdRange.KindItem, (ushort)min, (ushort)max));
            return merged.ToArray();
        }
    }

    /// <summary>
    /// The two ends differ in some content. An id inside a range that content occupies does not cross in either
    /// direction; every other id means the same on both sides. The result of a withheld id is that the item is absent
    /// on the other machine, never that it appears as something else.
    /// </summary>
    internal sealed class WithheldRangesItemIdPolicy : IPeerItemIdPolicy
    {
        private readonly SessionIdRange[] _ranges; // merged, ascending

        public WithheldRangesItemIdPolicy(SessionIdRange[] ranges) => _ranges = ranges ?? Array.Empty<SessionIdRange>();

        public bool TryToWire(int localId, out int wireId)   { wireId = localId; return !Withheld(localId); }
        public bool TryFromWire(int wireId, out int localId) { localId = wireId; return !Withheld(wireId); }

        private bool Withheld(int id)
        {
            foreach (SessionIdRange r in _ranges)
            {
                if (id < r.Min) return false;
                if (id <= r.Max) return true;
            }
            return false;
        }
    }
}
