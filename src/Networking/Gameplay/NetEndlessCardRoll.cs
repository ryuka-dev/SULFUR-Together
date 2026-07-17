using System;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// EM-6b-2 (host → all, Shared mode): the host's card RNG + card-manager selection state <b>captured just before</b> the
    /// host rolls a shared card-select event. The client sets its own Endless manager to this exact state and then runs the
    /// unmodified vanilla card roll (<c>FloatingCardManager.SetupCards</c>), so it reproduces byte-identical cards — same
    /// keys, same preselected items, same artwork — reusing 100% of the game's card code instead of re-rendering. The whole
    /// roll draws only from <c>gameplayRandom</c>, so syncing its <see cref="RandomState"/> plus the selection lists (which
    /// determine the available pool) is sufficient for exact parity; <see cref="ChoiceDraw"/> forces the card count to the
    /// host's meta-progression value.
    /// </summary>
    internal sealed class NetEndlessCardRoll
    {
        public string ChapterName = "";
        public int    LevelIndex;
        public bool   HasLevelSeed;
        public int    LevelSeed;
        public int    CardEventId; // correlates with the post-roll NetEndlessCardManifest of the same event

        public uint RandomState;                 // Unity.Mathematics.Random.state at roll time
        public int  ChoiceDraw;                  // EndlessModeManager.ChoiceDrawAmount (host meta-progression) → card count
        public int  RerollsRemaining;
        public int  BanishesRemaining;
        public int  BanishesUsedThisSession;

        public string[] SelectedOneTimeUsed   = Array.Empty<string>();
        public string[] BanishedCardKeys      = Array.Empty<string>();
        public string[] ForcedPreselectKeys   = Array.Empty<string>();
        public RemovedItem[] RemovedCardItems  = Array.Empty<RemovedItem>();

        internal struct RemovedItem { public string CardKey; public string ItemName; }
    }

    internal static class NetEndlessCardRollCodec
    {
        private const byte Version = 1;
        private const int  MaxList = 512;

        public static void Write(NetDataWriter w, NetEndlessCardRoll m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            w.Put(m.LevelSeed);
            w.Put(m.CardEventId);
            w.Put(m.RandomState);
            w.Put(m.ChoiceDraw);
            w.Put(m.RerollsRemaining);
            w.Put(m.BanishesRemaining);
            w.Put(m.BanishesUsedThisSession);
            WriteStrings(w, m.SelectedOneTimeUsed);
            WriteStrings(w, m.BanishedCardKeys);
            WriteStrings(w, m.ForcedPreselectKeys);
            int ri = m.RemovedCardItems != null ? Math.Min(m.RemovedCardItems.Length, MaxList) : 0;
            w.Put((ushort)ri);
            for (int i = 0; i < ri; i++)
            {
                w.Put(m.RemovedCardItems![i].CardKey ?? "");
                w.Put(m.RemovedCardItems[i].ItemName ?? "");
            }
        }

        public static bool TryRead(NetDataReader r, out NetEndlessCardRoll m)
        {
            m = new NetEndlessCardRoll();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.ChapterName             = r.GetString();
                m.LevelIndex              = r.GetInt();
                m.HasLevelSeed            = r.GetBool();
                m.LevelSeed               = r.GetInt();
                m.CardEventId             = r.GetInt();
                m.RandomState             = r.GetUInt();
                m.ChoiceDraw              = r.GetInt();
                m.RerollsRemaining        = r.GetInt();
                m.BanishesRemaining       = r.GetInt();
                m.BanishesUsedThisSession = r.GetInt();
                if (!ReadStrings(r, out m.SelectedOneTimeUsed)) return false;
                if (!ReadStrings(r, out m.BanishedCardKeys)) return false;
                if (!ReadStrings(r, out m.ForcedPreselectKeys)) return false;
                int ri = r.GetUShort();
                if (ri < 0 || ri > MaxList) return false;
                var items = new NetEndlessCardRoll.RemovedItem[ri];
                for (int i = 0; i < ri; i++)
                    items[i] = new NetEndlessCardRoll.RemovedItem { CardKey = r.GetString(), ItemName = r.GetString() };
                m.RemovedCardItems = items;
                return true;
            }
            catch { return false; }
        }

        private static void WriteStrings(NetDataWriter w, string[] arr)
        {
            int n = arr != null ? Math.Min(arr.Length, MaxList) : 0;
            w.Put((ushort)n);
            for (int i = 0; i < n; i++) w.Put(arr![i] ?? "");
        }

        private static bool ReadStrings(NetDataReader r, out string[] arr)
        {
            int n = r.GetUShort();
            if (n < 0 || n > MaxList) { arr = Array.Empty<string>(); return false; }
            arr = new string[n];
            for (int i = 0; i < n; i++) arr[i] = r.GetString();
            return true;
        }
    }
}
