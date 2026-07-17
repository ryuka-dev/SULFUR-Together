using System;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// EM-6b (host → all, Shared mode): the N cards the host rolled for one shared card-select event. The host owns the
    /// card RNG (the client's Endless manager is a slave with a diverged random stream), so the client can never roll the
    /// same cards — it must mirror this authoritative list to show the same options and to vote on them coherently. Sent
    /// once per event when the host's card panel finishes spawning (<c>FloatingCardManager.cardsSetup</c>).
    /// </summary>
    internal sealed class NetEndlessCardManifest
    {
        public string ChapterName = "";
        public int    LevelIndex;
        public bool   HasLevelSeed;
        public int    LevelSeed;
        public int    CardEventId; // monotonic per host run; correlates the manifest with its vote/result
        public CardEntry[] Cards = Array.Empty<CardEntry>();

        internal sealed class CardEntry
        {
            public string Key = "";     // CardReward.cardKey (or "" for the static Skip/Reroll cards)
            public bool   IsStatic;     // true = the Skip / Reroll cards at the tail of the list
            public string Title = "";   // host-rendered subtitle text (already localized + item-substituted)
            public string Desc = "";    // host-rendered description text
        }
    }

    internal static class NetEndlessCardManifestCodec
    {
        private const byte Version = 1;
        private const int  MaxCards = 16; // ChoiceDrawAmount is clamped to 2..4 (+2 static); 16 is a safe upper bound

        public static void Write(NetDataWriter w, NetEndlessCardManifest m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            w.Put(m.LevelSeed);
            w.Put(m.CardEventId);
            int count = m.Cards != null ? Math.Min(m.Cards.Length, MaxCards) : 0;
            w.Put((byte)count);
            for (int i = 0; i < count; i++)
            {
                var c = m.Cards![i] ?? new NetEndlessCardManifest.CardEntry();
                w.Put(c.Key ?? "");
                w.Put(c.IsStatic);
                w.Put(c.Title ?? "");
                w.Put(c.Desc ?? "");
            }
        }

        public static bool TryRead(NetDataReader r, out NetEndlessCardManifest m)
        {
            m = new NetEndlessCardManifest();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.ChapterName  = r.GetString();
                m.LevelIndex   = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                m.LevelSeed    = r.GetInt();
                m.CardEventId  = r.GetInt();
                int count = r.GetByte();
                if (count < 0 || count > MaxCards) return false;
                var cards = new NetEndlessCardManifest.CardEntry[count];
                for (int i = 0; i < count; i++)
                {
                    cards[i] = new NetEndlessCardManifest.CardEntry
                    {
                        Key      = r.GetString(),
                        IsStatic = r.GetBool(),
                        Title    = r.GetString(),
                        Desc     = r.GetString(),
                    };
                }
                m.Cards = cards;
                return true;
            }
            catch { return false; }
        }
    }
}
