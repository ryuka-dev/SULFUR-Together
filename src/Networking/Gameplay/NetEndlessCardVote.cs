using System;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// EM-6b-3a shared-mode card selection is a <b>1-of-N vote</b> over the host-rolled card set (the same card event as
    /// the 6b-1 manifest / 6b-2 roll). The host owns the tally and the clock; every end casts by aiming at an ordinary
    /// card and firing.
    ///
    /// <para><see cref="NetEndlessCardVoteState"/> (host → all) is the authoritative snapshot both ends render: which peer
    /// voted for which card index, the phase, and — once resolved — the single winning index that both ends apply via the
    /// vanilla <c>SpinAndDismissCard</c>. A monotonic <see cref="Revision"/> lets clients drop reordered re-sends. The
    /// countdown only runs after the first cast (<see cref="TimeoutActive"/>): early on, players may deliberate for a long
    /// time, so an un-cast vote never expires.</para>
    /// </summary>
    internal sealed class NetEndlessCardVoteState
    {
        public string ChapterName = "";
        public int    LevelIndex;
        public bool   HasLevelSeed;
        public int    LevelSeed;

        public int  CardEventId;   // correlates with the NetEndlessCardManifest / NetEndlessCardRoll of the same event
        public int  Revision;      // monotonic within a host run
        public byte  Phase;        // 0 = Active, 1 = Resolved
        public int  ResolvedIndex; // valid when Phase == Resolved: the winning ordinary-card index (real spawnedCards index)
        public bool  ResolvedByRoll; // the winner was a host tie/no-vote roll → both ends play the raffle animation first
        public bool  RaffleActive;   // EM-6b-3c: a tie draw is playing (host-driven) — both ends sweep TiedIndices, winner hidden until it ends
        public int  CardCount;     // total number of spawned cards this event (ordinary + Skip + Reroll)
        public bool  TimeoutActive; // true once at least one cast has landed → the countdown is running
        public float SecondsRemaining;
        public int[] TiedIndices = Array.Empty<int>(); // the cards a roll picks among (the raffle sweeps only these)
        public int[] BanishedIndices = Array.Empty<int>(); // EM-6b-3c: cards already banished this event (clients mirror removal)

        public Participant[] Participants = Array.Empty<Participant>();

        internal struct Participant
        {
            public string PeerId;
            public string Name;
            public int    VotedIndex;  // -1 = not voted for a pick yet
            public int    BanishIndex; // EM-6b-3c: -1 = not voting to banish; else the card index this peer wants banished
        }
    }

    /// <summary>EM-6b-3a/3c (client → host): "my player cast a vote in the current card event." <see cref="Kind"/> 0 = a
    /// pick vote for card <see cref="VotedIndex"/>; 1 = a banish vote targeting card <see cref="VotedIndex"/> (EM-6b-3c).
    /// Re-castable (a later cast of the same kind overwrites the earlier one; a banish cast on the already-targeted card
    /// retracts it). The sending peer is identified by the authenticated connection on the host, so no peer id travels.</summary>
    internal sealed class NetEndlessCardVoteCast
    {
        public int  CardEventId;
        public int  VotedIndex;
        public byte Kind; // 0 = pick, 1 = banish
    }

    internal static class NetEndlessCardVoteStateCodec
    {
        private const byte Version = 3; // v3: + RaffleActive (EM-6b-3c unified raffle)
        private const int  MaxParticipants = 16;

        public static void Write(NetDataWriter w, NetEndlessCardVoteState m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            w.Put(m.LevelSeed);
            w.Put(m.CardEventId);
            w.Put(m.Revision);
            w.Put(m.Phase);
            w.Put(m.ResolvedIndex);
            w.Put(m.ResolvedByRoll);
            w.Put(m.RaffleActive);
            w.Put(m.CardCount);
            w.Put(m.TimeoutActive);
            w.Put(m.SecondsRemaining);
            int ti = m.TiedIndices != null ? Math.Min(m.TiedIndices.Length, MaxParticipants) : 0;
            w.Put((ushort)ti);
            for (int i = 0; i < ti; i++) w.Put(m.TiedIndices![i]);
            int bi = m.BanishedIndices != null ? Math.Min(m.BanishedIndices.Length, MaxParticipants) : 0;
            w.Put((ushort)bi);
            for (int i = 0; i < bi; i++) w.Put(m.BanishedIndices![i]);
            int n = m.Participants != null ? Math.Min(m.Participants.Length, MaxParticipants) : 0;
            w.Put((ushort)n);
            for (int i = 0; i < n; i++)
            {
                w.Put(m.Participants![i].PeerId ?? "");
                w.Put(m.Participants[i].Name ?? "");
                w.Put(m.Participants[i].VotedIndex);
                w.Put(m.Participants[i].BanishIndex);
            }
        }

        public static bool TryRead(NetDataReader r, out NetEndlessCardVoteState m)
        {
            m = new NetEndlessCardVoteState();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.ChapterName      = r.GetString();
                m.LevelIndex       = r.GetInt();
                m.HasLevelSeed     = r.GetBool();
                m.LevelSeed        = r.GetInt();
                m.CardEventId      = r.GetInt();
                m.Revision         = r.GetInt();
                m.Phase            = r.GetByte();
                m.ResolvedIndex    = r.GetInt();
                m.ResolvedByRoll   = r.GetBool();
                m.RaffleActive     = r.GetBool();
                m.CardCount        = r.GetInt();
                m.TimeoutActive    = r.GetBool();
                m.SecondsRemaining = r.GetFloat();
                int ti = r.GetUShort();
                if (ti < 0 || ti > MaxParticipants) return false;
                var tied = new int[ti];
                for (int i = 0; i < ti; i++) tied[i] = r.GetInt();
                m.TiedIndices = tied;
                int bi = r.GetUShort();
                if (bi < 0 || bi > MaxParticipants) return false;
                var ban = new int[bi];
                for (int i = 0; i < bi; i++) ban[i] = r.GetInt();
                m.BanishedIndices = ban;
                int n = r.GetUShort();
                if (n < 0 || n > MaxParticipants) return false;
                var arr = new NetEndlessCardVoteState.Participant[n];
                for (int i = 0; i < n; i++)
                    arr[i] = new NetEndlessCardVoteState.Participant
                    {
                        PeerId = r.GetString(), Name = r.GetString(), VotedIndex = r.GetInt(), BanishIndex = r.GetInt(),
                    };
                m.Participants = arr;
                return true;
            }
            catch { return false; }
        }
    }

    internal static class NetEndlessCardVoteCastCodec
    {
        private const byte Version = 2; // v2: + Kind (EM-6b-3c)

        public static void Write(NetDataWriter w, NetEndlessCardVoteCast m)
        {
            w.Put(Version);
            w.Put(m.CardEventId);
            w.Put(m.VotedIndex);
            w.Put(m.Kind);
        }

        public static bool TryRead(NetDataReader r, out NetEndlessCardVoteCast m)
        {
            m = new NetEndlessCardVoteCast();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.CardEventId = r.GetInt();
                m.VotedIndex  = r.GetInt();
                m.Kind        = r.GetByte();
                return true;
            }
            catch { return false; }
        }
    }
}
