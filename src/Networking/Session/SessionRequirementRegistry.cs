using System;
using System.Collections.Generic;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// The session requirements registered on this machine. Owner of that list and of nothing else: it does not
    /// know about peers, comparisons or the wire.
    ///
    /// Plain static state with no dependency on the plugin having started, so a mod loaded before SULFUR Together can
    /// register from its own <c>Awake</c>. Main thread only.
    /// </summary>
    internal static class SessionRequirementRegistry
    {
        private sealed class Registration
        {
            public string Id = "";
            public string DisplayName = "";
            public Func<string?> Digest = null!;
            public SessionIdRange[] Claims = Array.Empty<SessionIdRange>();
        }

        private static readonly List<Registration> _entries = new List<Registration>();
        private static bool _snapshotting; // digest callbacks are running: the list is closed until they return

        public static int Count => _entries.Count;

        /// <summary>Add a requirement. False, with the reason logged, when it is refused.</summary>
        public static bool TryRegister(string id, string displayName, Func<string?> digest, SessionIdRange[] claims)
        {
            if (!SessionRequirementLimits.IsValidId(id))
                return Refuse(id, $"the id must be 1 to {SessionRequirementLimits.MaxIdLength} characters of [A-Za-z0-9._-]");
            if (digest == null)
                return Refuse(id, "the digest callback is null");
            if (_snapshotting)
                return Refuse(id, "registered from inside a digest callback");
            string trimmed = (displayName ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > SessionRequirementLimits.MaxDisplayNameChars)
                return Refuse(id, $"the display name must be 1 to {SessionRequirementLimits.MaxDisplayNameChars} characters");
            if (!SessionRequirementLimits.TrySanitizeDisplayName(trimmed, out string shownName))
                return Refuse(id, "the display name has no visible character");
            if (claims == null)
                return Refuse(id, "the claims are null");
            if (claims.Length > SessionRequirementLimits.MaxClaimsPerEntry)
                return Refuse(id, $"more than {SessionRequirementLimits.MaxClaimsPerEntry} id claims");
            if (CoopConnection.IsRunning)
                return Refuse(id, "a session is running; register at startup, before any session can start");
            if (_entries.Count >= SessionRequirementLimits.MaxEntries)
                return Refuse(id, $"{SessionRequirementLimits.MaxEntries} requirements are already registered");
            foreach (Registration existing in _entries)
                if (string.Equals(existing.Id, id, StringComparison.Ordinal))
                    return Refuse(id, "this id is already registered");

            int ceiling = VanillaItemIds.Ceiling;
            foreach (SessionIdRange c in claims)
            {
                if (c.Kind != SessionIdRange.KindItem)
                    return Refuse(id, $"unknown id kind {c.Kind}");
                if (c.Min > c.Max)
                    return Refuse(id, $"claim {c.Min}-{c.Max} has its ends the wrong way round");
                if (c.Min <= ceiling)
                    return Refuse(id, $"claim {c.Min}-{c.Max} reaches into the game's own item ids (1 to {ceiling})");
            }

            foreach (Registration existing in _entries)
                foreach (SessionIdRange theirs in existing.Claims)
                    foreach (SessionIdRange mine in claims)
                        if (mine.Kind == theirs.Kind && mine.Min <= theirs.Max && theirs.Min <= mine.Max)
                            SessionRequirementLog.Warn($"'{id}' claims {mine.Min}-{mine.Max}, which overlaps {theirs.Min}-{theirs.Max} of '{existing.Id}'. A difference in either one withholds the ids of both.");

            _entries.Add(new Registration
            {
                Id = id,
                DisplayName = shownName, // what every end shows: the same text a peer gets after it sanitises it
                Digest = digest,
                Claims = (SessionIdRange[])claims.Clone(),
            });
            SessionRequirementLog.Info($"registered '{id}' ({shownName}) claims=[{string.Join(", ", claims)}]");
            return true;
        }

        /// <summary>
        /// What this machine presents to a peer right now, in ordinal id order. Every digest callback runs once, here.
        /// One that throws, or returns something that is not a valid digest, leaves its entry without a digest, and an
        /// entry without a digest matches nothing.
        /// </summary>
        public static SessionRequirementEntry[] Snapshot()
        {
            if (_snapshotting) return Array.Empty<SessionRequirementEntry>(); // a callback asked for a snapshot: it gets none

            Registration[] regs = _entries.ToArray();
            Array.Sort(regs, (a, b) => string.CompareOrdinal(a.Id, b.Id));

            var result = new SessionRequirementEntry[regs.Length];
            _snapshotting = true;
            try { FillSnapshot(regs, result); }
            finally { _snapshotting = false; }
            return result;
        }

        private static void FillSnapshot(Registration[] regs, SessionRequirementEntry[] result)
        {
            for (int i = 0; i < regs.Length; i++)
            {
                Registration r = regs[i];
                string? digest = null;
                try
                {
                    string? value = r.Digest();
                    if (SessionRequirementLimits.IsValidDigest(value)) digest = value;
                    else SessionRequirementLog.Error($"'{r.Id}' gave no valid digest (1 to {SessionRequirementLimits.MaxDigestLength} printable ASCII characters, no spaces); it will not match any peer.");
                }
                catch (Exception ex)
                {
                    SessionRequirementLog.Error($"the digest callback of '{r.Id}' threw {ex.GetType().Name}: {ex.Message}; it will not match any peer.");
                }
                result[i] = new SessionRequirementEntry(r.Id, r.DisplayName, digest, (SessionIdRange[])r.Claims.Clone());
            }
        }

        private static bool Refuse(string? id, string reason)
        {
            SessionRequirementLog.Error($"refused to register '{Printable(id)}': {reason}.");
            return false;
        }

        // An id that failed validation can hold anything; keep it from breaking or faking a log line.
        internal static string Printable(string? id)
        {
            if (id == null) return "<null>";
            var sb = new System.Text.StringBuilder();
            foreach (char c in id)
            {
                if (sb.Length >= SessionRequirementLimits.MaxIdLength) { sb.Append("..."); break; }
                sb.Append(SessionRequirementLimits.IsIdChar(c) ? c : '?');
            }
            return sb.ToString();
        }
    }
}
