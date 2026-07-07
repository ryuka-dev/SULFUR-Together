using System.Collections.Generic;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>Host→Clients broadcast payload for the finalized end-of-Run statistics (RS-2). RunSeq is monotonic
    /// per Host session so a client can ignore a stale or duplicate resend.</summary>
    internal sealed class NetRunStatsList
    {
        public int RunSeq { get; set; }
        public List<NetRunStats> Players { get; set; } = new List<NetRunStats>();
    }
}
