using System.Text;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Lightweight debug-timeline helper.
    /// Generates compact log prefixes that let Host and Client logs be correlated by
    /// seq, hostIdx, rosterRev, and time — without emitting a wall of text on every call.
    ///
    /// Usage:
    ///   NetLogger.Info($"{NetDbg.Ctx("HealthState", seq: evt.Sequence, hostIdx: evt.HostSpawnIndex, rev: rosterRev)} sent hp=...");
    /// </summary>
    internal static class NetDbg
    {
        /// <summary>Returns a compact prefix [t=… f=… role=… msg=… seq=… hostIdx=… rev=…].</summary>
        public static string Ctx(
            string?  msg     = null,
            int?     seq     = null,
            int?     hostIdx = null,
            int?     rev     = null,
            float?   sendAt  = null)
        {
            string role = NetConfig.GetMode() == NetMode.Host ? "Host" : "Client";
            float  t    = Time.realtimeSinceStartup;
            int    f    = Time.frameCount;

            var sb = new StringBuilder(64);
            sb.Append("[t=");
            sb.Append(t.ToString("F3"));
            sb.Append("s f=");
            sb.Append(f);
            sb.Append(" role=");
            sb.Append(role);
            if (msg     != null) { sb.Append(" msg=");    sb.Append(msg);          }
            if (seq     != null) { sb.Append(" seq=");    sb.Append(seq.Value);    }
            if (hostIdx != null) { sb.Append(" hostIdx="); sb.Append(hostIdx.Value); }
            if (rev     != null) { sb.Append(" rev=");    sb.Append(rev.Value);    }
            if (sendAt  != null)
            {
                float ageMs = (t - sendAt.Value) * 1000f;
                sb.Append(" ageMs=");
                sb.Append(ageMs.ToString("F0"));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Returns ms since the given host-send timestamp (for latency annotation).</summary>
        public static string AgeMs(float sentAt) =>
            $"ageMs={(( Time.realtimeSinceStartup - sentAt) * 1000f):F0}";
    }
}
