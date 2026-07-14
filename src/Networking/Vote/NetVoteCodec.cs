using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Vote
{
    /// <summary>Wire codecs for the VOTE-1 messages. Kept tolerant (try/catch → false) like the other ST codecs so a
    /// malformed packet is dropped, never thrown.</summary>
    internal static class NetVoteCodec
    {
        // ---- ClientVoteStart (client → host): just the kind being proposed ------------------------------------
        public static void WriteStart(NetDataWriter w, VoteKind kind) => w.Put((byte)kind);

        public static bool TryReadStart(NetDataReader r, out VoteKind kind)
        {
            kind = VoteKind.None;
            try { kind = (VoteKind)r.GetByte(); return true; }
            catch { return false; }
        }

        // ---- ClientVoteCast (client → host): kind + this client's choice --------------------------------------
        public static void WriteCast(NetDataWriter w, VoteKind kind, VoteChoice choice)
        {
            w.Put((byte)kind);
            w.Put((byte)choice);
        }

        public static bool TryReadCast(NetDataReader r, out VoteKind kind, out VoteChoice choice)
        {
            kind = VoteKind.None; choice = VoteChoice.None;
            try
            {
                kind   = (VoteKind)r.GetByte();
                choice = (VoteChoice)r.GetByte();
                return true;
            }
            catch { return false; }
        }

        // ---- HostVoteState (host → all): the full authoritative snapshot --------------------------------------
        public static void WriteState(NetDataWriter w, VoteStateSnapshot s)
        {
            w.Put(s.Revision);
            w.Put(s.HasVote);
            w.Put((byte)s.Kind);
            w.Put((byte)s.Rule);
            w.Put((byte)s.Phase);
            w.Put((byte)s.Outcome);
            w.Put(s.InitiatorPeerId ?? "");
            w.Put(s.SecondsRemaining);
            w.Put((ushort)s.Participants.Count);
            foreach (var p in s.Participants)
            {
                w.Put(p.PeerId ?? "");
                w.Put(p.Name ?? "");
                w.Put((byte)p.Choice);
            }
        }

        public static bool TryReadState(NetDataReader r, out VoteStateSnapshot result)
        {
            result = null!;
            try
            {
                var s = new VoteStateSnapshot
                {
                    Revision        = r.GetInt(),
                    HasVote         = r.GetBool(),
                    Kind            = (VoteKind)r.GetByte(),
                    Rule            = (VoteRule)r.GetByte(),
                    Phase           = (VotePhase)r.GetByte(),
                    Outcome         = (VoteOutcome)r.GetByte(),
                    InitiatorPeerId = r.GetString(),
                    SecondsRemaining = r.GetFloat(),
                };
                int count = r.GetUShort();
                for (int i = 0; i < count; i++)
                {
                    s.Participants.Add(new VoteParticipant
                    {
                        PeerId = r.GetString(),
                        Name   = r.GetString(),
                        Choice = (VoteChoice)r.GetByte(),
                    });
                }
                result = s;
                return true;
            }
            catch { return false; }
        }
    }
}
