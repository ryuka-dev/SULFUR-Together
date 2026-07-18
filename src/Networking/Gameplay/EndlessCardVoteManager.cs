using System;
using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-6b-3a: the shared-mode Endless card selection is a <b>host-authoritative 1-of-N vote</b>. See
    /// Docs/EndlessModeSyncPlan.md §6-7. It builds on the 6b-2 roll mirror (both ends already show the same 3D cards):
    /// instead of the vanilla "whoever fires first picks the card", every player casts a vote for the card they are
    /// aiming at, the host tallies, and the resolved index is applied on both ends via the vanilla
    /// <c>SpinAndDismissCard</c> (personal effects land on each <c>PlayerUnit</c>; world-card duplication is the known
    /// EM-7 gap).
    ///
    /// <para>The host owns the tally and the clock; clients only display the snapshot and forward casts (same shape as
    /// <see cref="Vote.CoopVoteManager"/>, but N-option rather than binary — the binary path is untouched). Resolution:
    /// a strict majority wins; a tie is broken by a host roll; if nobody voted by the deadline the host rolls uniformly.
    /// The countdown only starts after the <b>first cast</b> (<see cref="_timeoutActive"/>) — early on, players may
    /// deliberate indefinitely, so an un-cast vote never expires.</para>
    ///
    /// <para>EM-6b-3b: the static <b>Skip</b> and <b>Reroll</b> cards are votable too (the votable set is every
    /// <i>interactable</i> card, so an exhausted 0-reroll card is excluded). A resolved Skip runs its personal reward on
    /// each player; a resolved Reroll is <b>host-authoritative</b> — the host re-rolls (which broadcasts a fresh
    /// roll/manifest/vote) while the client waits for and mirrors that new panel rather than rolling its own. The per-card
    /// banish (DismissButton) is still deferred (EM-6b-3c). All state is main-thread (host tick from the service tick,
    /// client apply from the network dispatch, local casts from the card-pick prefix).</para>
    /// </summary>
    internal static class EndlessCardVoteManager
    {
        private const float TimeoutSeconds   = 20f;  // countdown length AFTER the first cast lands
        private const float KeepaliveSeconds = 1f;   // periodic resend so a late snapshot self-heals
        private const float RaffleSeconds    = 1.7f; // tie/no-vote roll: raffle-sweep the cards before applying the winner
        private const int   RaffleLoops      = 3;    // how many times the cursor sweeps the card row before landing

        private static bool Enabled  { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn    { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }
        private static bool SharedMode => EndlessSyncManager.IsIndependentMode == false;

        // ---- current snapshot (both roles; the stamp UI reads this) -------------------------------------------
        private static NetEndlessCardVoteState? _current;
        public static NetEndlessCardVoteState? Current => _current;

        /// <summary>True while a shared card vote is live or resolved for the current run — the card-pick prefix routes
        /// the local pick into a cast (and blocks the vanilla pick) whenever this is set.</summary>
        public static bool SharedVoteActive =>
            NetGameplaySyncBridge.BossMode != NetMode.Off && SharedMode && _current != null && _current.CardEventId > 0;

        // ---- HOST authoritative state ------------------------------------------------------------------------
        private sealed class Participant { public string PeerId = ""; public string Name = ""; public int VotedIndex = -1; }

        private static bool   _hostActive;      // a host vote is open (Active or Resolved-pending-teardown)
        private static bool   _hostResolved;
        private static int    _hostEventId;
        private static int    _hostCardCount;
        private static int    _hostResolvedIndex;
        private static bool   _hostResolvedByRoll;
        private static int[]  _hostTied = System.Array.Empty<int>();
        private static int[]  _hostVotable = System.Array.Empty<int>(); // EM-6b-3b: the interactable card indices (excludes a 0-reroll card)
        private static bool   _timeoutActive;   // the countdown started (first cast landed)
        private static float  _timeoutStart;
        private static float  _lastBroadcast;
        private static string _hostChap = ""; private static int _hostLvl; private static bool _hostHasSeed; private static int _hostSeed;
        private static readonly List<Participant> _participants = new List<Participant>();
        private static int _revision;

        // ---- apply guard + raffle animation (both roles) -----------------------------------------------------
        private static int   _appliedEvent = -1;
        private static bool  _raffleActive;
        private static int   _raffleEvent = -1;
        private static int   _raffleTarget;
        private static int[] _raffleTied = System.Array.Empty<int>(); // the cards the sweep cycles through (the tied set)
        private static float _raffleStart;

        /// <summary>True while the tie/no-vote raffle sweep is playing (both ends) — the overlay pulses the cursor card.</summary>
        public static bool RaffleActive => _raffleActive;

        /// <summary>The card index the raffle cursor is currently over — a decelerating sweep that cycles <b>only through the
        /// tied cards</b> and lands on the winner.</summary>
        public static int RaffleCursorIndex
        {
            get
            {
                if (!_raffleActive || _raffleTied.Length == 0) return -1;
                int n = _raffleTied.Length;
                int targetPos = System.Array.IndexOf(_raffleTied, _raffleTarget);
                if (targetPos < 0) targetPos = 0;
                float x = Mathf.Clamp01((Now - _raffleStart) / RaffleSeconds);
                float ease = 1f - (1f - x) * (1f - x) * (1f - x); // easeOutCubic: fast → slow
                int totalSteps = n * RaffleLoops + targetPos; // lands on the winner's position at x=1
                int step = Mathf.FloorToInt(totalSteps * ease);
                return _raffleTied[((step % n) + n) % n];
            }
        }

        public static void Reset()
        {
            _current = null;
            _hostActive = false; _hostResolved = false; _hostEventId = 0; _hostCardCount = 0; _hostResolvedIndex = -1;
            _hostResolvedByRoll = false;
            _timeoutActive = false; _timeoutStart = 0f; _lastBroadcast = 0f;
            _hostChap = ""; _hostLvl = 0; _hostHasSeed = false; _hostSeed = 0;
            _participants.Clear();
            _revision = 0;
            _appliedEvent = -1;
            _raffleActive = false; _raffleEvent = -1; _raffleTarget = 0; _raffleTied = System.Array.Empty<int>(); _raffleStart = 0f;
            _hostTied = System.Array.Empty<int>();
            _hostVotable = System.Array.Empty<int>();
        }

        private static bool IsVotable(int index)
        {
            for (int i = 0; i < _hostVotable.Length; i++) if (_hostVotable[i] == index) return true;
            return false;
        }

        private static float Now => Time.realtimeSinceStartup; // world is frozen during shared card select → use realtime

        // ================================================================== HOST: open / tick / cast / resolve

        /// <summary>HOST: open the shared card vote for a freshly-rolled panel (called from
        /// <see cref="EndlessCardManager.HostTick"/> right after the manifest broadcast). <paramref name="cardCount"/> is the
        /// total number of spawned cards (ordinary + Skip + Reroll); <paramref name="votable"/> is the subset of interactable
        /// card indices players may vote for (EM-6b-3b — excludes a 0-reroll card). No-op if nothing is votable.</summary>
        public static void HostOpenVote(int eventId, int cardCount, int[] votable, string chap, int lvl, bool hasSeed, int seed)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                if (cardCount <= 0 || votable == null || votable.Length == 0) { if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3b no votable cards event={eventId} — vote not opened"); return; }
                if (_hostActive && _hostEventId == eventId) return; // already open for this event

                _hostActive = true; _hostResolved = false;
                _hostEventId = eventId; _hostCardCount = cardCount; _hostResolvedIndex = -1;
                _hostVotable = votable;
                _timeoutActive = false; _timeoutStart = 0f; _lastBroadcast = 0f;
                _hostChap = chap ?? ""; _hostLvl = lvl; _hostHasSeed = hasSeed; _hostSeed = seed;

                RebuildParticipants();
                if (_participants.Count == 0) { _hostActive = false; return; }

                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3b host card vote OPEN event={eventId} cards={cardCount} votable=[{string.Join(",", votable)}] participants={_participants.Count}");
                HostBroadcast();
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostOpenVote failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST (each service tick): drive the timeout, keepalive, and end-of-vote (panel torn down).</summary>
        public static void HostTick()
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (!_hostActive)
                {
                    if (_current != null && _current.CardEventId != 0) _current = null; // host: no vote → nothing to show
                    return;
                }
                if (!Enabled || !SharedMode) { HostEndVote(); return; }

                float now = Now;

                // The panel is gone (SpinAndDismissCard finished, or an abnormal close) → the vote is over.
                if (!EndlessCardManager.LocalCardPanelUp()) { HostEndVote(); return; }

                if (!_hostResolved)
                {
                    PruneDisconnected();
                    HostEvaluate(now);
                    if (!_hostResolved && now - _lastBroadcast >= KeepaliveSeconds) HostBroadcast();
                }
                else if (now - _lastBroadcast >= KeepaliveSeconds)
                {
                    HostBroadcast(); // keep re-asserting the resolved index until the panel finishes tearing down
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-6b-3a HostTick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: record a participant's cast (from a client message or the host's own pick) and re-evaluate.</summary>
        public static void HostHandleCast(string peerId, int eventId, int votedIndex)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                if (!_hostActive || _hostResolved || eventId != _hostEventId) return;
                if (string.IsNullOrEmpty(peerId)) return;
                if (!IsVotable(votedIndex)) return; // only an interactable card index (ordinary / Skip / interactable Reroll)

                var p = Find(peerId);
                if (p == null) return;            // not a recognized participant
                if (p.VotedIndex == votedIndex) return; // no change
                p.VotedIndex = votedIndex;

                float now = Now;
                if (!_timeoutActive) { _timeoutActive = true; _timeoutStart = now; } // first cast → start the clock
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a cast peer={peerId} index={votedIndex} event={eventId}");

                HostEvaluate(now);
                if (!_hostResolved) HostBroadcast();
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostHandleCast failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void HostEvaluate(float now)
        {
            if (!_hostActive || _hostResolved) return;
            int total = _participants.Count;
            if (total == 0) { HostEndVote(); return; }

            int voted = 0;
            foreach (var p in _participants) if (p.VotedIndex >= 0) voted++;

            bool allVoted = voted >= total;
            bool deadline = _timeoutActive && now >= _timeoutStart + TimeoutSeconds;
            if (!allVoted && !deadline) return;

            HostResolve(now);
        }

        private static void HostResolve(float now)
        {
            // Tally votes per ordinary card index.
            var tally = new int[_hostCardCount];
            int cast = 0;
            foreach (var p in _participants)
                if (p.VotedIndex >= 0 && p.VotedIndex < _hostCardCount) { tally[p.VotedIndex]++; cast++; }

            int index;
            bool byRoll;
            List<int> tied = new List<int>();
            if (cast == 0)
            {
                foreach (int idx in _hostVotable) tied.Add(idx);                                       // nobody voted → sweep all votable
                if (tied.Count == 0) for (int i = 0; i < _hostCardCount; i++) tied.Add(i);             // (defensive; votable is never empty here)
                index = tied[UnityEngine.Random.Range(0, tied.Count)];
                byRoll = true;
            }
            else
            {
                int best = -1;
                for (int i = 0; i < tally.Length; i++)
                {
                    if (tally[i] > best) { best = tally[i]; tied.Clear(); tied.Add(i); }
                    else if (tally[i] == best && best > 0) tied.Add(i);
                }
                byRoll = tied.Count > 1;                                                              // a genuine tie
                index = byRoll ? tied[UnityEngine.Random.Range(0, tied.Count)] : tied[0];             // tie → host roll
            }

            _hostResolved = true;
            _hostResolvedIndex = Mathf.Clamp(index, 0, _hostCardCount - 1);
            _hostResolvedByRoll = byRoll;
            _hostTied = tied.ToArray();
            if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a host card vote RESOLVED event={_hostEventId} index={_hostResolvedIndex} cast={cast}/{_participants.Count} byRoll={byRoll} tied=[{string.Join(",", _hostTied)}]");
            HostBroadcast();
            ApplyResolvedLocally(_hostEventId, _hostResolvedIndex, byRoll, _hostTied); // the host applies its own copy
        }

        private static void HostEndVote()
        {
            if (!_hostActive && _current == null) return;
            _hostActive = false; _hostResolved = false; _timeoutActive = false;
            _participants.Clear();
            _current = null; // both the host's own display and the broadcast clear
            var snap = new NetEndlessCardVoteState { CardEventId = 0, Revision = ++_revision };
            NetGameplaySyncBridge.BroadcastHostEndlessCardVoteState(snap);
            if (LogOn) Plugin.Log.Info("[Endless] EM-6b-3a host card vote ENDED");
        }

        private static void HostBroadcast()
        {
            var snap = BuildHostSnapshot();
            _current = snap;                 // host renders its own snapshot
            NetGameplaySyncBridge.BroadcastHostEndlessCardVoteState(snap);
            _lastBroadcast = Now;
        }

        private static NetEndlessCardVoteState BuildHostSnapshot()
        {
            float secs = _timeoutActive && !_hostResolved
                ? Mathf.Max(0f, _timeoutStart + TimeoutSeconds - Now)
                : 0f;
            var snap = new NetEndlessCardVoteState
            {
                ChapterName = _hostChap, LevelIndex = _hostLvl, HasLevelSeed = _hostHasSeed, LevelSeed = _hostSeed,
                CardEventId = _hostEventId, Revision = ++_revision,
                Phase = (byte)(_hostResolved ? 1 : 0), ResolvedIndex = _hostResolvedIndex, ResolvedByRoll = _hostResolvedByRoll,
                CardCount = _hostCardCount, TimeoutActive = _timeoutActive, SecondsRemaining = secs,
                TiedIndices = _hostTied,
            };
            var arr = new NetEndlessCardVoteState.Participant[_participants.Count];
            for (int i = 0; i < _participants.Count; i++)
                arr[i] = new NetEndlessCardVoteState.Participant
                { PeerId = _participants[i].PeerId, Name = _participants[i].Name, VotedIndex = _participants[i].VotedIndex };
            snap.Participants = arr;
            return snap;
        }

        // ================================================================== CLIENT: apply snapshot

        /// <summary>CLIENT: apply a host card-vote snapshot (dedup by revision; validated against the local run).</summary>
        public static void ClientApplySnapshot(NetEndlessCardVoteState snap)
        {
            try
            {
                if (!Enabled || snap == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (snap.CardEventId != 0 && Boss.NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, snap.ChapterName, StringComparison.Ordinal) || lvl != snap.LevelIndex))
                    return; // different level

                if (_current != null && snap.Revision <= _current.Revision && snap.CardEventId == _current.CardEventId) return;
                _current = snap.CardEventId == 0 ? null : snap;

                if (snap.CardEventId != 0 && snap.Phase == 1) // resolved → apply on the client too
                    ApplyResolvedLocally(snap.CardEventId, snap.ResolvedIndex, snap.ResolvedByRoll, snap.TiedIndices);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ClientApplySnapshot failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== local cast (card-pick prefix, both roles)

        /// <summary>BOTH ROLES: the local player pressed Fire/Interact while the shared card panel is up. Read the card
        /// they are aiming at; if it is a votable (interactable) card — ordinary, Skip, or an available Reroll — cast a vote
        /// for it. The per-card banish button (DismissButton) is still ignored (EM-6b-3c). Called from the card-pick prefix,
        /// which always blocks the vanilla pick in shared mode.</summary>
        public static void OnLocalPickInput(object fcm)
        {
            try
            {
                if (!Enabled || !SharedMode || _current == null || _current.CardEventId <= 0 || _current.Phase != 0) return;
                if (!EndlessCardManager.TryReadAimedVotableCard(fcm, _current.CardCount, out int index)) return;

                if (NetGameplaySyncBridge.BossMode == NetMode.Host)
                    HostHandleCast(NetGameplaySyncBridge.LocalPeerId, _current.CardEventId, index);
                else
                    NetGameplaySyncBridge.SendEndlessCardVoteCast(_current.CardEventId, index);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] OnLocalPickInput failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== apply (both roles, once per event)

        private static void ApplyResolvedLocally(int eventId, int resolvedIndex, bool byRoll, int[] tied)
        {
            if (eventId == _appliedEvent) return;
            if (_raffleActive && _raffleEvent == eventId) return; // already sweeping toward this event's winner

            // A tie/no-vote roll picks the winner instantly, which reads as a bug ("my pick was ignored"). Play a short
            // decelerating raffle sweep across ONLY the tied cards, landing on the winner, then apply. A clear majority is
            // obvious from the stamps, so it applies immediately.
            if (byRoll && tied != null && tied.Length > 1 && RaffleSeconds > 0f)
            {
                _raffleActive = true; _raffleEvent = eventId; _raffleTarget = resolvedIndex;
                _raffleTied = tied; _raffleStart = Now;
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a raffle start event={eventId} target={_raffleTarget} tied=[{string.Join(",", tied)}]");
                return;
            }

            _appliedEvent = eventId;
            EndlessCardManager.ApplyResolvedPick(resolvedIndex);
            if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a applied resolved pick event={eventId} index={resolvedIndex}");
        }

        /// <summary>BOTH ROLES (each service tick): advance the tie-break raffle and apply the winner when the sweep ends.
        /// Cheap no-op when no raffle is running.</summary>
        public static void FrameTick()
        {
            if (!_raffleActive) return;
            try
            {
                if (Now < _raffleStart + RaffleSeconds) return; // still sweeping
                _raffleActive = false;
                if (_raffleEvent != _appliedEvent)
                {
                    _appliedEvent = _raffleEvent;
                    EndlessCardManager.ApplyResolvedPick(_raffleTarget);
                    if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a raffle applied event={_raffleEvent} index={_raffleTarget}");
                }
            }
            catch (Exception ex) { _raffleActive = false; Plugin.Log.Warn($"[Endless] EM-6b-3a FrameTick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== participants

        private static void RebuildParticipants()
        {
            _participants.Clear();
            var svc = CoopConnection.Service;
            if (svc == null) return;
            foreach (var s in svc.SessionSnapshot)
            {
                if (s.State != NetConnectionState.Connected) continue;
                _participants.Add(new Participant { PeerId = s.PeerId, Name = s.PlayerName, VotedIndex = -1 });
            }
        }

        // Drop participants that are no longer connected so "all voted" still resolves after a disconnect. Never adds
        // new joiners to an in-progress card vote (a mid-run join is gated out anyway).
        private static void PruneDisconnected()
        {
            var svc = CoopConnection.Service;
            if (svc == null) return;
            var connected = new HashSet<string>();
            foreach (var s in svc.SessionSnapshot) if (s.State == NetConnectionState.Connected) connected.Add(s.PeerId);
            for (int i = _participants.Count - 1; i >= 0; i--)
                if (!connected.Contains(_participants[i].PeerId)) _participants.RemoveAt(i);
        }

        private static Participant? Find(string peerId)
        {
            foreach (var p in _participants) if (p.PeerId == peerId) return p;
            return null;
        }
    }
}
