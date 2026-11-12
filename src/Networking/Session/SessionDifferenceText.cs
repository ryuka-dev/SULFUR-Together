using System.Text;

namespace SULFURTogether.Networking.Session
{
    /// <summary>How a comparison's differences are written out for the log and, shortened, for a toast or an error
    /// line. Every entry is named by its display name and its id: two entries can carry the same name, and a peer can
    /// choose a name that reads like another mod's.</summary>
    internal static class SessionDifferenceText
    {
        /// <summary>Entries named in a line for the player before the rest is counted.</summary>
        private const int MaxNamedForPlayer = 3;

        public static string ForLog(SessionContentComparison comparison)
        {
            var sb = new StringBuilder();
            foreach (SessionDifference d in comparison.Differences)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(d.Id).Append(" (").Append(d.DisplayName).Append(") ").Append(d.Kind);
            }
            sb.Append(" | withheld item ids: ");
            if (comparison.WithheldItemRanges.Length == 0) sb.Append("none");
            for (int i = 0; i < comparison.WithheldItemRanges.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(comparison.WithheldItemRanges[i].Min).Append('-').Append(comparison.WithheldItemRanges[i].Max);
            }
            return sb.ToString();
        }

        /// <summary>"Name [id], Name [id], Name [id] +2".</summary>
        public static string ForPlayer(SessionContentComparison comparison)
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (SessionDifference d in comparison.Differences)
            {
                if (shown == MaxNamedForPlayer) break;
                if (shown > 0) sb.Append(", ");
                sb.Append(Label(d));
                shown++;
            }
            int rest = comparison.Differences.Length - shown;
            if (rest > 0) sb.Append(" +").Append(rest);
            return sb.ToString();
        }

        /// <summary>One entry as the player reads it. The id is left out only when the name already is the id.</summary>
        public static string Label(SessionDifference d)
            => string.Equals(d.DisplayName, d.Id, System.StringComparison.Ordinal) ? d.Id : d.DisplayName + " [" + d.Id + "]";
    }
}
