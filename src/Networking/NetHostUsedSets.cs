using System.Collections.Generic;
using System.Linq;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-I: snapshot of GameManager's cross-level generation-input "used" sets.
    /// These exclusion sets are deterministic generation inputs alongside the level seed:
    ///   - usedChunksThisRun           (chunks already used in this run)
    ///   - usedUniqueEventThisRun      (unique events already used in this run)
    ///   - usedUniqueEventThisEnvironment (unique events already used in this environment)
    /// Elements are stored as string keys (their natural string form / Unity object name) so they
    /// survive serialization; the apply path only re-populates collections whose element type is string.
    /// </summary>
    public sealed class NetHostUsedSets
    {
        public List<string> UsedChunksThisRun { get; set; } = new List<string>();
        public List<string> UsedEventsThisRun { get; set; } = new List<string>();
        public List<string> UsedEventsThisEnvironment { get; set; } = new List<string>();

        /// <summary>True only when these values were actually read from a live GameManager.</summary>
        public bool Captured { get; set; }

        public int ChunksCount      => UsedChunksThisRun?.Count ?? 0;
        public int EventsRunCount   => UsedEventsThisRun?.Count ?? 0;
        public int EventsEnvCount   => UsedEventsThisEnvironment?.Count ?? 0;

        public NetHostUsedSets Clone() => new NetHostUsedSets
        {
            Captured = Captured,
            UsedChunksThisRun         = new List<string>(UsedChunksThisRun ?? new List<string>()),
            UsedEventsThisRun         = new List<string>(UsedEventsThisRun ?? new List<string>()),
            UsedEventsThisEnvironment = new List<string>(UsedEventsThisEnvironment ?? new List<string>()),
        };

        public string ToCompactString()
            => $"captured={Captured} chunks={ChunksCount} eventsRun={EventsRunCount} eventsEnv={EventsEnvCount}";

        /// <summary>Bounded, log-friendly summary of a key list: "[a,b,c,+N]".</summary>
        public static string Summary(List<string>? items, int max = 8)
        {
            if (items == null || items.Count == 0) return "[]";
            var head = items.Take(max);
            string more = items.Count > max ? $",+{items.Count - max}" : "";
            return "[" + string.Join(",", head) + more + "]";
        }
    }
}
