using System.Collections.Generic;

namespace SULFURTogether.Networking.Vote
{
    /// <summary>What a vote decides. The first (issue #8) is <see cref="EnableDevMode"/>; the subsystem is generic
    /// so later features register a new kind + a resolve callback and reuse everything else.</summary>
    public enum VoteKind : byte
    {
        None          = 0,
        EnableDevMode = 1,
    }

    /// <summary>How a vote resolves.
    /// <list type="bullet">
    /// <item><b>Unanimous</b> — every participant must agree. Any explicit decline fails it immediately; at timeout
    /// the players who never voted are counted as agreeing (AFK = consent).</item>
    /// <item><b>Majority</b> — whichever side first passes a strict majority (&gt; 50%, never exactly 50%) wins, or
    /// the vote resolves at timeout (non-voters count as agreeing; an exact tie fails).</item>
    /// </list></summary>
    public enum VoteRule : byte { Unanimous = 0, Majority = 1 }

    public enum VoteChoice  : byte { None = 0, Agree = 1, Decline = 2 }
    public enum VotePhase   : byte { Active = 0, Resolved = 1 }
    public enum VoteOutcome : byte { None = 0, Passed = 1, Failed = 2, Cancelled = 3 }

    public sealed class VoteParticipant
    {
        public string     PeerId = "";
        public string     Name   = "";
        public VoteChoice Choice = VoteChoice.None;
    }

    /// <summary>
    /// Host-authoritative snapshot of the current vote — the single wire + display record. The overlay reads it and
    /// derives everything (agree fraction for the border, per-player squares, countdown text) so there is one source
    /// of truth for both the visual and the animation (the DR lesson).
    /// </summary>
    public sealed class VoteStateSnapshot
    {
        public int         Revision;
        public bool        HasVote;          // false => the vote has fully ended; clients fade the overlay out
        public VoteKind    Kind;
        public VoteRule    Rule;
        public VotePhase   Phase;
        public VoteOutcome Outcome;
        public string      InitiatorPeerId = "";
        public float       SecondsRemaining; // display countdown while Active
        public readonly List<VoteParticipant> Participants = new List<VoteParticipant>();

        public int Total => Participants.Count;

        public int AgreeCount
        {
            get { int n = 0; foreach (var p in Participants) if (p.Choice == VoteChoice.Agree)   n++; return n; }
        }

        public int DeclineCount
        {
            get { int n = 0; foreach (var p in Participants) if (p.Choice == VoteChoice.Decline) n++; return n; }
        }

        public int NotVotedCount
        {
            get { int n = 0; foreach (var p in Participants) if (p.Choice == VoteChoice.None)    n++; return n; }
        }

        /// <summary>Fraction of participants who agreed (0..1) — the yellow border fill.</summary>
        public float AgreeFraction => Total > 0 ? (float)AgreeCount / Total : 0f;
        /// <summary>Fraction who declined (0..1) — the red border fill in Majority; the fail overlay in Unanimous.</summary>
        public float DeclineFraction => Total > 0 ? (float)DeclineCount / Total : 0f;
    }
}
