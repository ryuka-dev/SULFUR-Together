using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// Host → clients: every team mark the host holds right now, as (peer, host spawn index) pairs. Sent whole when the
    /// set changes and again every few seconds, so a client that missed one or bound a copy late catches up. A mark
    /// that has moved on but still lingers on its old enemy is listed there too, as it still counts.
    /// </summary>
    internal sealed class NetHostTeamMarks
    {
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }
        public int    Revision     { get; set; }
        public List<Entry> Entries { get; } = new List<Entry>();

        public struct Entry
        {
            public string PeerId;
            public int HostSpawnIndex;
        }

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (HasLevelSeed && localState.HasLevelSeed && localState.LevelSeed != LevelSeed) return false;
            return true;
        }
    }

    internal static class NetHostTeamMarksCodec
    {
        public const byte Version = 1;
        private const int MaxStringLength = 256;
        private const int MaxEntries = 64;

        public static void Write(NetDataWriter w, NetHostTeamMarks m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Revision);
            int count = System.Math.Min(m.Entries.Count, MaxEntries);
            w.Put((byte)count);
            for (int i = 0; i < count; i++)
            {
                var e = m.Entries[i];
                w.Put(e.PeerId ?? "");
                w.Put(e.HostSpawnIndex);
            }
        }

        public static bool TryRead(NetDataReader r, out NetHostTeamMarks m)
        {
            m = null!;
            try
            {
                if (r.GetByte() != Version) return false;
                var x = new NetHostTeamMarks { ChapterName = r.GetString(), LevelIndex = r.GetInt(), HasLevelSeed = r.GetBool() };
                if (x.ChapterName == null || x.ChapterName.Length > MaxStringLength) return false;
                if (x.HasLevelSeed) x.LevelSeed = r.GetInt();
                x.Revision = r.GetInt();
                int count = r.GetByte();
                if (count > MaxEntries) return false;
                for (int i = 0; i < count; i++)
                {
                    string peer = r.GetString();
                    if (peer == null || peer.Length == 0 || peer.Length > MaxStringLength) return false;
                    int idx = r.GetInt();
                    if (idx <= 0) return false;
                    x.Entries.Add(new NetHostTeamMarks.Entry { PeerId = peer, HostSpawnIndex = idx });
                }
                if (r.AvailableBytes != 0) return false;
                m = x;
                return true;
            }
            catch { return false; }
        }
    }
}
