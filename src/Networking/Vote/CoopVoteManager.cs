using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace SULFURTogether.Networking.Vote
{
    /// <summary>
    /// VOTE-1: the generic, host-authoritative session-vote subsystem (issue #8's dev-mode gate is the first
    /// consumer). Modelled on the DR rescue protocol — the <b>host owns the tally and the clock</b>, clients only
    /// display the snapshot and forward their cast. Anyone may propose; the host validates and starts.
    ///
    /// Two rules (<see cref="VoteRule"/>): Unanimous (dev mode) and Majority (future consumers). Every vote runs a
    /// single <see cref="TimeoutSeconds"/> clock: players who never vote count as agreeing at timeout (AFK = consent),
    /// an explicit decline fails a Unanimous vote at once, and a strict majority ends a Majority vote early. Every
    /// end (pass / fail / cancel) is held on screen for <see cref="ResidualSeconds"/> before the overlay fades, so no
    /// one misses the result. A failed vote puts its kind on a <see cref="FailCooldownSeconds"/> cooldown.
    ///
    /// This class is transport-agnostic: it calls <see cref="CoopConnection"/> / <see cref="NetService"/> seams to
    /// send, and reads participants from the session roster — no LiteNetLib types cross into it.
    /// </summary>
    internal static class CoopVoteManager
    {
        public const float TimeoutSeconds      = 15f;
        public const float ResidualSeconds     = 5f;
        public const float FailCooldownSeconds = 10f;
        private const float BroadcastIntervalSeconds = 1f;

        // ---- current snapshot (both roles; the overlay reads this) --------------------------------------------
        private static VoteStateSnapshot _current;
        public static VoteStateSnapshot Current => _current;

        /// <summary>True while a vote is live or in its post-result residual (used to gate a second proposal).</summary>
        public static bool HasCurrentVote => _current != null && _current.HasVote;

        /// <summary>The local peer's id ("host" on the host, the assigned id on a client) — identifies the local
        /// participant so the overlay can highlight its own square and the input can cast for it.</summary>
        public static string LocalPeerId
        {
            get
            {
                var svc = CoopConnection.Service;
                if (svc == null) return "";
                if (CoopConnection.CurrentMode == NetMode.Host) return "host";
                foreach (var s in svc.SessionSnapshot) if (s.IsLocal) return s.PeerId;
                return "";
            }
        }

        // ---- HOST authoritative state ------------------------------------------------------------------------
        private sealed class HostVote
        {
            public VoteKind    Kind;
            public VoteRule    Rule;
            public string      InitiatorPeerId = "";
            public float       StartTime;
            public VotePhase   Phase   = VotePhase.Active;
            public VoteOutcome Outcome = VoteOutcome.None;
            public float       ResolvedAt;
            public readonly List<VoteParticipant> Participants = new List<VoteParticipant>();

            public VoteParticipant Find(string peerId)
            {
                foreach (var p in Participants) if (p.PeerId == peerId) return p;
                return null;
            }
        }

        private static HostVote _hostVote;
        private static int _revision;
        private static readonly Dictionary<VoteKind, float> _cooldownUntil = new Dictionary<VoteKind, float>();
        private static float _lastBroadcast;

        private static VoteRule RuleForKind(VoteKind kind)
        {
            switch (kind)
            {
                case VoteKind.EnableDevMode: return VoteRule.Unanimous; // dev mode requires everyone (anti-grief)
                default:                     return VoteRule.Unanimous;
            }
        }

        // ---------------------------------------------------------------- lifecycle

        public static void Reset()
        {
            _hostVote = null;
            _current = null;
            _revision = 0;
            _cooldownUntil.Clear();
            _lastBroadcast = 0f;
        }

        // ---------------------------------------------------------------- proposing (either role)

        /// <summary>Can the local player usefully propose the dev-mode vote right now? (Display gate for the connect
        /// page; the host re-validates authoritatively.)</summary>
        public static bool CanProposeDevModeVote()
            => CoopConnection.CurrentMode != NetMode.Off
               && !NetSessionSettings.DeveloperModeEnabled
               && !HasCurrentVote;

        /// <summary>Local player proposes the dev-mode vote (connect-page button). Host starts locally; client asks
        /// the host.</summary>
        public static void ProposeDevModeVote(float now)
        {
            if (CoopConnection.CurrentMode == NetMode.Host)
                HostStart(VoteKind.EnableDevMode, "host", now);
            else if (CoopConnection.CurrentMode == NetMode.Client)
                CoopConnection.Service?.SendVoteStart(VoteKind.EnableDevMode);
        }

        // ---------------------------------------------------------------- HOST: start / cast / evaluate

        /// <summary>Host handles a client's start request (validated inside <see cref="HostStart"/>).</summary>
        public static void HostHandleClientStart(string peerId, VoteKind kind, float now)
            => HostStart(kind, peerId, now);

        private static void HostStart(VoteKind kind, string initiatorPeerId, float now)
        {
            if (CoopConnection.CurrentMode != NetMode.Host) return;
            if (_hostVote != null) return;                                   // one vote at a time (incl. residual)
            if (_cooldownUntil.TryGetValue(kind, out var until) && now < until)
            {
                Plugin.Log?.Info($"[Vote] start rejected — {kind} on cooldown ({until - now:0.0}s left)");
                return;
            }

            var svc = CoopConnection.Service;
            if (svc == null) return;

            var v = new HostVote { Kind = kind, Rule = RuleForKind(kind), InitiatorPeerId = initiatorPeerId, StartTime = now };
            foreach (var s in svc.SessionSnapshot)
            {
                if (s.State != NetConnectionState.Connected) continue;
                v.Participants.Add(new VoteParticipant { PeerId = s.PeerId, Name = s.PlayerName, Choice = VoteChoice.None });
            }
            if (v.Participants.Count == 0) return;

            var init = v.Find(initiatorPeerId);
            if (init != null) init.Choice = VoteChoice.Agree;               // the proposer implicitly agrees

            _hostVote = v;
            Plugin.Log?.Info($"[Vote] started kind={kind} rule={v.Rule} initiator={initiatorPeerId} participants={v.Participants.Count}");
            HostEvaluate(now);                                              // may resolve at once (e.g. lone proposer)
            HostBroadcast(now);
        }

        /// <summary>Host records a participant's cast and re-evaluates.</summary>
        public static void HostHandleCast(string peerId, VoteKind kind, VoteChoice choice, float now)
        {
            if (_hostVote == null || _hostVote.Phase != VotePhase.Active) return;
            if (_hostVote.Kind != kind) return;
            if (choice != VoteChoice.Agree && choice != VoteChoice.Decline) return;
            var p = _hostVote.Find(peerId);
            if (p == null || p.Choice == choice) return;
            p.Choice = choice;
            Plugin.Log?.Info($"[Vote] cast peer={peerId} choice={choice}");
            HostEvaluate(now);
            HostBroadcast(now);
        }

        /// <summary>Membership changed while a vote was live — the consensus is void. Cancel it (no cooldown), show
        /// the cancelled result briefly, then fade.</summary>
        public static void HostOnMembershipChanged(float now)
        {
            if (_hostVote == null || _hostVote.Phase != VotePhase.Active) return;
            _hostVote.Phase = VotePhase.Resolved;
            _hostVote.Outcome = VoteOutcome.Cancelled;
            _hostVote.ResolvedAt = now;
            Plugin.Log?.Info("[Vote] cancelled — session membership changed");
            HostBroadcast(now);
        }

        private static void HostEvaluate(float now)
        {
            var v = _hostVote;
            if (v == null || v.Phase != VotePhase.Active) return;

            int total = v.Participants.Count;
            int agree = 0, decline = 0;
            foreach (var p in v.Participants)
            {
                if (p.Choice == VoteChoice.Agree) agree++;
                else if (p.Choice == VoteChoice.Decline) decline++;
            }
            int notVoted = total - agree - decline;
            bool deadline = now >= v.StartTime + TimeoutSeconds;

            if (v.Rule == VoteRule.Unanimous)
            {
                if (decline > 0)      { HostResolve(VoteOutcome.Failed, now); return; }
                if (agree == total)   { HostResolve(VoteOutcome.Passed, now); return; }
                if (deadline)         { HostResolve(VoteOutcome.Passed, now); return; } // non-voters = agree
            }
            else // Majority
            {
                if (agree * 2 > total)   { HostResolve(VoteOutcome.Passed, now); return; }
                if (decline * 2 > total) { HostResolve(VoteOutcome.Failed, now); return; }
                if (deadline)
                {
                    int effAgree = agree + notVoted;                        // AFK counts as agree
                    HostResolve(effAgree > decline ? VoteOutcome.Passed : VoteOutcome.Failed, now); // tie => fail
                    return;
                }
            }
        }

        private static void HostResolve(VoteOutcome outcome, float now)
        {
            var v = _hostVote;
            v.Phase = VotePhase.Resolved;
            v.Outcome = outcome;
            v.ResolvedAt = now;
            if (outcome == VoteOutcome.Failed)
                _cooldownUntil[v.Kind] = now + FailCooldownSeconds;
            Plugin.Log?.Info($"[Vote] resolved kind={v.Kind} outcome={outcome}");
            OnResolved(v.Kind, outcome);
        }

        /// <summary>Wire a passed vote to its consumer. Extend here for future vote kinds.</summary>
        private static void OnResolved(VoteKind kind, VoteOutcome outcome)
        {
            if (kind == VoteKind.EnableDevMode && outcome == VoteOutcome.Passed)
                CoopDevAuthority.HostApplyVoteResult(true);
        }

        // ---------------------------------------------------------------- HOST: tick + broadcast

        public static void HostTick(float now)
        {
            if (CoopConnection.CurrentMode != NetMode.Host || _hostVote == null) return;

            if (_hostVote.Phase == VotePhase.Active)
            {
                var before = _hostVote.Phase;
                HostEvaluate(now);
                if (_hostVote == null) return;
                if (_hostVote.Phase != before)
                    HostBroadcast(now);                                     // just resolved at the deadline — push the result
                else if (now - _lastBroadcast >= BroadcastIntervalSeconds)
                    HostBroadcast(now);                                     // keep the countdown fresh
            }
            else if (now >= _hostVote.ResolvedAt + ResidualSeconds)
            {
                HostEndVote(now);
            }
        }

        private static void HostEndVote(float now)
        {
            _hostVote = null;
            var snap = new VoteStateSnapshot { Revision = ++_revision, HasVote = false };
            _current = snap;
            CoopConnection.Service?.BroadcastVoteState(snap);
            Plugin.Log?.Info("[Vote] ended (overlay fade)");
        }

        private static void HostBroadcast(float now)
        {
            var v = _hostVote;
            if (v == null) return;
            var snap = new VoteStateSnapshot
            {
                Revision         = ++_revision,
                HasVote          = true,
                Kind             = v.Kind,
                Rule             = v.Rule,
                Phase            = v.Phase,
                Outcome          = v.Outcome,
                InitiatorPeerId  = v.InitiatorPeerId,
                SecondsRemaining = v.Phase == VotePhase.Active
                    ? UnityEngine.Mathf.Max(0f, v.StartTime + TimeoutSeconds - now)
                    : 0f,
            };
            foreach (var p in v.Participants)
                snap.Participants.Add(new VoteParticipant { PeerId = p.PeerId, Name = p.Name, Choice = p.Choice });

            _current = snap;                                                // host displays its own snapshot
            CoopConnection.Service?.BroadcastVoteState(snap);
            _lastBroadcast = now;
        }

        // ---------------------------------------------------------------- CLIENT: apply snapshot

        public static void ClientApplySnapshot(VoteStateSnapshot snap)
        {
            if (snap == null) return;
            if (_current != null && snap.Revision <= _current.Revision) return; // drop stale/reordered
            _current = snap;
        }

        // ---------------------------------------------------------------- input (Y = agree, N = decline)

        public static void TickInput(float now)
        {
            var cur = _current;
            if (cur == null || !cur.HasVote || cur.Phase != VotePhase.Active) return;

            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.yKey.wasPressedThisFrame)      LocalCast(VoteChoice.Agree,   now);
            else if (kb.nKey.wasPressedThisFrame) LocalCast(VoteChoice.Decline, now);
        }

        private static void LocalCast(VoteChoice choice, float now)
        {
            var cur = _current;
            if (cur == null) return;
            if (CoopConnection.CurrentMode == NetMode.Host)
                HostHandleCast("host", cur.Kind, choice, now);
            else
                CoopConnection.Service?.SendVoteCast(cur.Kind, choice);
        }
    }
}
