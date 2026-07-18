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
    /// roll/manifest/vote) while the client waits for and mirrors that new panel rather than rolling its own.</para>
    ///
    /// <para>EM-6b-3c: <b>banishing a card is one of the unified vote options</b>, not a separate vote. Every player casts
    /// exactly one vote — pick a card (ordinary / Skip / Reroll) OR banish a card — and any option is a toggle (re-cast to
    /// retract). Resolution counts every cast option (a pick and a banish of the same card are distinct), takes the
    /// most-voted, and breaks a tie by host roll. A pick / Skip / Reroll winner resolves the panel (with the tie-break
    /// raffle when rolled); a <b>banish</b> winner runs the vanilla <c>DismissCard</c> (consuming a shared banish; clients
    /// mirror the removal), then re-opens the same vote on the remaining cards. The countdown runs only while at least one
    /// vote is cast — retracting to zero stops it. All state is main-thread (host tick from the service tick, client apply
    /// from the network dispatch, local casts from the card-pick prefix).</para>
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
        private sealed class Participant { public string PeerId = ""; public string Name = ""; public int VotedIndex = -1; public int BanishIndex = -1; }

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
        private static readonly List<int> _banishedIndices = new List<int>(); // EM-6b-3c: cards banished this event (broadcast)
        private static int _revision;

        // ---- CLIENT banish mirror bookkeeping ----------------------------------------------------------------
        private static int _clientBanishEvent = -1;
        private static readonly HashSet<int> _clientBanishedMirrored = new HashSet<int>();

        // ---- apply guard (pick) + tie-draw animation ---------------------------------------------------------
        // The raffle is host-driven: on any tie (pick / Skip / Reroll / banish, incl. a same-card pick-vs-banish tie) the
        // host plays a short draw before applying, so a rolled outcome is never abrupt and the winner is hidden until it
        // lands. Both ends animate it from the snapshot's RaffleActive + TiedIndices; the host applies the winner (a pick
        // resolves the panel; a banish removes the card and re-opens the vote) when its timer elapses.
        private static int   _appliedEvent = -1;
        private static bool  _raffleActive;  // local visual flag (host: while raffling; client: mirrors snapshot.RaffleActive)
        private static int[] _raffleTied = System.Array.Empty<int>(); // the distinct cards the draw cycles through
        private static float _raffleStart;   // local start time of the current draw (set on the rising edge)

        // HOST raffle authority
        private static bool  _hostRaffling;
        private static float _hostRaffleStart;
        private static int   _hostRaffleWinIdx;
        private static bool  _hostRaffleWinBanish;
        private static int[] _hostRaffleTied = System.Array.Empty<int>();

        /// <summary>True while a tie draw is playing (both ends) — the overlay + the card-highlight postfix animate it.</summary>
        public static bool RaffleActive => _raffleActive;

        /// <summary>The card the draw cursor is currently over. A multi-card tie decelerates card-to-card and lands on the
        /// winner's card; a single-card tie (a pick-vs-banish tie on one card) blinks that card a few times.</summary>
        public static int RaffleCursorIndex
        {
            get
            {
                if (!_raffleActive || _raffleTied.Length == 0) return -1;
                float x = Mathf.Clamp01((Now - _raffleStart) / RaffleSeconds);
                float ease = 1f - (1f - x) * (1f - x) * (1f - x); // easeOutCubic: fast → slow
                int n = _raffleTied.Length;
                if (n == 1)
                {
                    const int blinks = 5;
                    bool on = x >= 1f || Mathf.FloorToInt(ease * blinks) % 2 == 0; // blink, land ON
                    return on ? _raffleTied[0] : -1;
                }
                int targetPos = n - 1; // the winner is placed last in _raffleTied so the sweep lands on it at x=1
                int totalSteps = n * RaffleLoops + targetPos;
                int step = Mathf.FloorToInt(totalSteps * ease);
                return _raffleTied[((step % n) + n) % n];
            }
        }

        // Set the local draw visual (both roles): rising edge stamps the local start + tied cards.
        private static void SetLocalRaffle(bool active, int[]? tied)
        {
            if (active && !_raffleActive) { _raffleStart = Now; _raffleTied = tied ?? System.Array.Empty<int>(); }
            _raffleActive = active;
            if (!active) _raffleTied = System.Array.Empty<int>();
        }

        public static void Reset()
        {
            _current = null;
            _hostActive = false; _hostResolved = false; _hostEventId = 0; _hostCardCount = 0; _hostResolvedIndex = -1;
            _hostResolvedByRoll = false;
            _timeoutActive = false; _timeoutStart = 0f; _lastBroadcast = 0f;
            _hostChap = ""; _hostLvl = 0; _hostHasSeed = false; _hostSeed = 0;
            _participants.Clear();
            _banishedIndices.Clear();
            _clientBanishEvent = -1; _clientBanishedMirrored.Clear();
            _revision = 0;
            _appliedEvent = -1;
            _raffleActive = false; _raffleTied = System.Array.Empty<int>(); _raffleStart = 0f;
            _hostRaffling = false; _hostRaffleStart = 0f; _hostRaffleWinIdx = -1; _hostRaffleWinBanish = false; _hostRaffleTied = System.Array.Empty<int>();
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
                _banishedIndices.Clear();

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

                // A tie draw is playing → apply its winner when the timer elapses (a pick resolves; a banish removes + re-votes).
                if (_hostRaffling)
                {
                    if (now >= _hostRaffleStart + RaffleSeconds) HostApplyRaffleWinner(now);
                    else if (now - _lastBroadcast >= KeepaliveSeconds) HostBroadcast();
                    return;
                }

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

        /// <summary>HOST: record a participant's <b>pick</b> vote for a card (ordinary / Skip / Reroll) and re-evaluate.
        /// One vote per player: a pick clears any prior banish; re-casting the same pick retracts it (toggle).</summary>
        public static void HostHandleCast(string peerId, int eventId, int votedIndex)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                if (!_hostActive || _hostResolved || _hostRaffling || eventId != _hostEventId) return; // votes locked during the draw
                if (string.IsNullOrEmpty(peerId)) return;
                if (!IsVotable(votedIndex)) return; // only an interactable card index (ordinary / Skip / interactable Reroll)

                var p = Find(peerId);
                if (p == null) return;            // not a recognized participant
                bool retract = p.VotedIndex == votedIndex && p.BanishIndex < 0;
                p.VotedIndex  = retract ? -1 : votedIndex; // toggle off if re-casting the same pick
                p.BanishIndex = -1;                        // one vote per player: a pick clears the banish
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a cast peer={peerId} index={p.VotedIndex} event={eventId}");

                float now = Now;
                UpdateCountdown(now);
                HostEvaluate(now);
                if (!_hostResolved) HostBroadcast();
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostHandleCast failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST (EM-6b-3c): record a participant's <b>banish</b> vote — one of the unified vote options. One vote
        /// per player: a banish clears any prior pick; re-casting the same target retracts it (toggle).</summary>
        public static void HostHandleBanishCast(string peerId, int eventId, int targetIndex)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                if (!_hostActive || _hostResolved || _hostRaffling || eventId != _hostEventId) return; // votes locked during the draw
                if (string.IsNullOrEmpty(peerId)) return;
                object? fcm = EndlessCardManager.ResolveLocalCardManager();
                if (fcm == null || !EndlessCardManager.IsBanishableCard(fcm, targetIndex)) return;

                var p = Find(peerId);
                if (p == null) return;
                bool retract = p.BanishIndex == targetIndex;
                p.BanishIndex = retract ? -1 : targetIndex; // toggle off if re-casting the same banish
                p.VotedIndex  = -1;                          // one vote per player: a banish clears the pick
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3c banish cast peer={peerId} target={p.BanishIndex} event={eventId}");

                float now = Now;
                UpdateCountdown(now);
                HostEvaluate(now);
                if (!_hostResolved) HostBroadcast();
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostHandleBanishCast failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static int CountVotes()
        {
            int v = 0;
            foreach (var p in _participants) if (p.VotedIndex >= 0 || p.BanishIndex >= 0) v++;
            return v;
        }

        // The countdown runs only while at least one vote is cast (user decision): retracting to zero votes stops it, and
        // the next vote restarts it from full.
        private static void UpdateCountdown(float now)
        {
            int votes = CountVotes();
            if (votes > 0 && !_timeoutActive) { _timeoutActive = true; _timeoutStart = now; }
            else if (votes == 0 && _timeoutActive) { _timeoutActive = false; _timeoutStart = 0f; }
        }

        private static void HostEvaluate(float now)
        {
            if (!_hostActive || _hostResolved) return;
            int total = _participants.Count;
            if (total == 0) { HostEndVote(); return; }

            UpdateCountdown(now);
            int voted = CountVotes();
            if (voted == 0) return; // no votes → nothing to resolve (and no countdown)

            bool allVoted = voted >= total;
            bool deadline = _timeoutActive && now >= _timeoutStart + TimeoutSeconds;
            if (!allVoted && !deadline) return;

            HostResolve(now);
        }

        // Unified resolution: pick / Skip / Reroll / banish are all one vote per player. Tally every cast option (a pick of
        // card i and a banish of card i are distinct options), take the most-voted, break a tie by host roll. A banish
        // winner removes the card and re-opens the same vote on the remaining cards; a pick/Skip/Reroll winner resolves the
        // panel (with the tie-break raffle when it was rolled).
        private static void HostResolve(float now)
        {
            var pickTally   = new int[_hostCardCount];
            var banishTally = new int[_hostCardCount];
            foreach (var p in _participants)
            {
                if (p.VotedIndex >= 0 && p.VotedIndex < _hostCardCount) pickTally[p.VotedIndex]++;
                else if (p.BanishIndex >= 0 && p.BanishIndex < _hostCardCount) banishTally[p.BanishIndex]++;
            }

            // Collect the winning option(s): the maximum-vote options across both pick and banish tallies.
            int best = 0;
            var tied = new List<(int index, bool banish)>();
            for (int i = 0; i < _hostCardCount; i++)
            {
                if (pickTally[i] > best)   { best = pickTally[i];   tied.Clear(); tied.Add((i, false)); }
                else if (pickTally[i] == best && best > 0) tied.Add((i, false));
            }
            for (int i = 0; i < _hostCardCount; i++)
            {
                if (banishTally[i] > best) { best = banishTally[i]; tied.Clear(); tied.Add((i, true)); }
                else if (banishTally[i] == best && best > 0) tied.Add((i, true));
            }

            if (best == 0 || tied.Count == 0) // defensive: no votes → uniform pick roll over the votable set
            {
                foreach (int idx in _hostVotable) tied.Add((idx, false));
                if (tied.Count == 0) for (int i = 0; i < _hostCardCount; i++) tied.Add((i, false));
            }

            bool byRoll = tied.Count > 1;
            var winner = byRoll ? tied[UnityEngine.Random.Range(0, tied.Count)] : tied[0];
            // A clear pick applies at once (obvious from the stamps); a tie OR any banish gets a short reveal draw first, so
            // a rolled outcome isn't abrupt and a banish's ✕ stamps are seen before the card leaves.
            bool draw = byRoll || winner.banish;
            if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a host card vote RESOLVED event={_hostEventId} winner={(winner.banish ? "banish " : "pick ")}{winner.index} byRoll={byRoll} draw={draw} options=[{string.Join(",", tied.ConvertAll(o => (o.banish ? "b" : "p") + o.index))}]");

            if (!draw) { HostApplyPick(winner.index, now); return; }

            // Play a host-driven draw over the distinct tied cards, winner hidden until it lands. Place the winning card LAST
            // so the deceleration lands on it. A single tied card (a clear banish, or a pick-vs-banish tie on one card) blinks.
            var tiedCards = new List<int>();
            foreach (var o in tied) if (o.index != winner.index && !tiedCards.Contains(o.index)) tiedCards.Add(o.index);
            tiedCards.Add(winner.index); // winner last

            _hostRaffling = true; _hostRaffleStart = now;
            _hostRaffleWinIdx = winner.index; _hostRaffleWinBanish = winner.banish;
            _hostRaffleTied = tiedCards.ToArray();
            SetLocalRaffle(true, _hostRaffleTied); // host's own draw visual
            if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a host draw start event={_hostEventId} winner={(winner.banish ? "banish " : "pick ")}{winner.index} cards=[{string.Join(",", _hostRaffleTied)}]");
            HostBroadcast();
        }

        // HOST: the draw timer elapsed — apply the winner it landed on.
        private static void HostApplyRaffleWinner(float now)
        {
            _hostRaffling = false;
            SetLocalRaffle(false, null);
            if (_hostRaffleWinBanish) HostResolveBanish(_hostRaffleWinIdx, now);
            else                      HostApplyPick(_hostRaffleWinIdx, now);
        }

        // HOST: a pick / Skip / Reroll won — resolve the panel. Both ends run SpinAndDismissCard (host here, clients on the
        // Resolved snapshot).
        private static void HostApplyPick(int index, float now)
        {
            _hostResolved = true;
            _hostResolvedIndex = Mathf.Clamp(index, 0, _hostCardCount - 1);
            _hostResolvedByRoll = false;
            _hostTied = System.Array.Empty<int>();
            HostBroadcast();
            ApplyResolvedPick(_hostEventId, _hostResolvedIndex); // the host applies its own copy
        }

        // HOST: a banish won — remove the card (host-authoritative DismissCard, clients mirror via BanishedIndices) and
        // re-open the SAME vote on the remaining cards: clear every vote and stop the countdown (it restarts on the next
        // cast). Does NOT resolve the panel. If the banish can't run (out of shared banishes), it just clears the votes.
        private static void HostResolveBanish(int index, float now)
        {
            bool banished = EndlessCardManager.HostBanishCard(index); // vanilla DismissCard (consumes a shared banish)
            if (banished)
            {
                _banishedIndices.Add(index);
                _hostVotable = System.Array.FindAll(_hostVotable, i => i != index);
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3c host banished card index={index} event={_hostEventId} banishesLeft={EndlessCardManager.HostBanishesRemaining()}");
            }
            foreach (var p in _participants) { p.VotedIndex = -1; p.BanishIndex = -1; } // fresh round
            _hostResolved = false;                       // stays Active — the vote continues on the remaining cards
            _timeoutActive = false; _timeoutStart = 0f;  // 0 votes → no countdown
            HostBroadcast();
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
                Phase = (byte)(_hostResolved ? 1 : 0),
                ResolvedIndex = _hostRaffling ? -1 : _hostResolvedIndex, // winner hidden while the draw plays
                ResolvedByRoll = _hostResolvedByRoll,
                RaffleActive = _hostRaffling,
                CardCount = _hostCardCount, TimeoutActive = _timeoutActive, SecondsRemaining = secs,
                TiedIndices = _hostRaffling ? _hostRaffleTied : _hostTied,
                BanishedIndices = _banishedIndices.ToArray(),
            };
            var arr = new NetEndlessCardVoteState.Participant[_participants.Count];
            for (int i = 0; i < _participants.Count; i++)
                arr[i] = new NetEndlessCardVoteState.Participant
                { PeerId = _participants[i].PeerId, Name = _participants[i].Name, VotedIndex = _participants[i].VotedIndex, BanishIndex = _participants[i].BanishIndex };
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

                SetLocalRaffle(snap.CardEventId != 0 && snap.RaffleActive, snap.TiedIndices); // mirror the host-driven draw
                ClientMirrorBanishes(snap); // EM-6b-3c: remove any newly-banished card locally

                if (snap.CardEventId != 0 && snap.Phase == 1) // pick resolved → apply on the client too
                    ApplyResolvedPick(snap.CardEventId, snap.ResolvedIndex);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ClientApplySnapshot failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT (EM-6b-3c): mirror any card the host has banished this event that we have not removed yet.</summary>
        private static void ClientMirrorBanishes(NetEndlessCardVoteState snap)
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Client || snap == null || snap.CardEventId == 0) return;
            if (snap.BanishedIndices == null || snap.BanishedIndices.Length == 0) return;
            if (_clientBanishEvent != snap.CardEventId) { _clientBanishEvent = snap.CardEventId; _clientBanishedMirrored.Clear(); }
            foreach (int idx in snap.BanishedIndices)
            {
                if (_clientBanishedMirrored.Contains(idx)) continue;
                if (!EndlessCardManager.ClientMirrorBanish(idx)) continue; // panel not ready yet → retry on the next snapshot
                _clientBanishedMirrored.Add(idx);
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3c client mirrored banish index={idx} event={snap.CardEventId}");
            }
        }

        // ================================================================== local cast (card-pick prefix, both roles)

        /// <summary>BOTH ROLES: the local player pressed Fire/Interact while the shared card panel is up. Aiming at a card's
        /// dismiss button casts a banish vote; aiming at a card body casts a pick vote (both toggle — one vote per player).
        /// Called from the card-pick prefix, which always blocks the vanilla pick in shared mode.</summary>
        public static void OnLocalPickInput(object fcm)
        {
            try
            {
                if (!Enabled || !SharedMode || _current == null || _current.CardEventId <= 0 || _current.Phase != 0) return;

                // EM-6b-3c: aiming at a card's dismiss button + firing is a BANISH vote (checked first — it's a distinct
                // aim from the card body). Otherwise it is a normal pick vote.
                if (EndlessCardManager.TryReadAimedBanishCard(fcm, out int banishIndex))
                {
                    if (NetGameplaySyncBridge.BossMode == NetMode.Host)
                        HostHandleBanishCast(NetGameplaySyncBridge.LocalPeerId, _current.CardEventId, banishIndex);
                    else
                        NetGameplaySyncBridge.SendEndlessCardVoteCast(_current.CardEventId, banishIndex, 1);
                    return;
                }

                if (!EndlessCardManager.TryReadAimedVotableCard(fcm, _current.CardCount, out int index)) return;

                if (NetGameplaySyncBridge.BossMode == NetMode.Host)
                    HostHandleCast(NetGameplaySyncBridge.LocalPeerId, _current.CardEventId, index);
                else
                    NetGameplaySyncBridge.SendEndlessCardVoteCast(_current.CardEventId, index, 0);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] OnLocalPickInput failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== apply (both roles, once per event)

        // Apply the resolved PICK (Skip/Reroll included) via the vanilla SpinAndDismissCard, once per event. The draw, when
        // there was one, has already played out (host-driven); this is only reached with the final winner.
        private static void ApplyResolvedPick(int eventId, int resolvedIndex)
        {
            if (eventId == _appliedEvent || resolvedIndex < 0) return;
            _appliedEvent = eventId;
            EndlessCardManager.ApplyResolvedPick(resolvedIndex);
            if (LogOn) Plugin.Log.Info($"[Endless] EM-6b-3a applied resolved pick event={eventId} index={resolvedIndex}");
        }

        /// <summary>BOTH ROLES (each service tick): kept for the service-tick contract; the tie draw is now host-driven
        /// (see <see cref="HostTick"/>) and rendered from the snapshot, so there is nothing to advance here.</summary>
        public static void FrameTick() { }

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
