using System;
using System.Collections.Generic;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// The joining player's decision about a host whose content differs from theirs. Owner of three things: whether a
    /// decision is being waited for, what was decided, and which decisions were already given in this process.
    ///
    /// While a decision is pending the client is connected to the host and not linked: it stays in its own world, and
    /// the link to the host already withholds the ids of the differing content. Nothing here loads a level or touches
    /// the network; the owning service is told the outcome and acts on it.
    ///
    /// The prompt is shown by whoever registered <see cref="IsPromptSurfaceOpen"/> (the connect page). Without one
    /// there is nowhere to ask, and the service does not join at all.
    ///
    /// Lifetime: cleared whenever networking is started, stopped or switched (<c>CoopConnection.Apply</c>), so no
    /// pending state outlives the service it belongs to. Accepted decisions live for the process and are never stored.
    /// </summary>
    internal static class NetContentConsent
    {
        internal enum Outcome { None, Accepted, Cancelled }

        /// <summary>A prompt that never appears is given up on after this long.</summary>
        private const float PromptAppearSeconds = 3f;
        /// <summary>A shown prompt that is gone for this long counts as closed. Covers the page rebuilding itself.</summary>
        private const float PromptGoneSeconds = 0.75f;
        /// <summary>A question left unanswered this long is withdrawn. The host has counted the player as joined since
        /// the handshake (player list, votes), so the wait cannot be open-ended.</summary>
        private const float DecisionSeconds = 180f;

        private static readonly HashSet<string> _accepted = new HashSet<string>(StringComparer.Ordinal);

        private static object? _owner;           // the service that asked; a decision is only ever delivered to it
        private static string _key = "";
        private static bool _promptShown;
        private static float _since;
        private static float _goneSince = -1f;
        private static Outcome _decision;         // set by the prompt, carried out on the next Tick
        private static string _cancelReason = "";

        /// <summary>Set once by the connect page: whether it is on screen and able to show the prompt right now.
        /// Null when no such surface exists in this install.</summary>
        public static Func<bool>? IsPromptSurfaceOpen;

        public static bool IsPending => _owner != null;

        /// <summary>What the prompt shows. Null when nothing is pending.</summary>
        public static SessionContentComparison? PendingComparison { get; private set; }
        public static string PendingHostName { get; private set; } = "";

        /// <summary>How the last pending decision ended, for the prompt surface to close or restore itself.</summary>
        public static Outcome LastOutcome { get; private set; }

        public static bool CanPrompt
        {
            get
            {
                try { return IsPromptSurfaceOpen != null && IsPromptSurfaceOpen(); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Whether the player already agreed to exactly these differences with this room in this process.</summary>
        public static bool HasConsent(string roomToken, SessionContentComparison comparison)
            => _accepted.Contains(Key(roomToken, comparison));

        /// <summary>Start waiting for the player's decision on behalf of <paramref name="owner"/>.</summary>
        public static void Begin(object owner, string roomToken, string hostName, SessionContentComparison comparison, float now)
        {
            _owner = owner;
            _key = Key(roomToken, comparison);
            PendingComparison = comparison;
            PendingHostName = hostName ?? "";
            _promptShown = false;
            _since = now;
            _goneSince = -1f;
            _decision = Outcome.None;
            _cancelReason = "";
            LastOutcome = Outcome.None;
        }

        /// <summary>The prompt surface built the prompt for the pending decision.</summary>
        public static void MarkPromptShown()
        {
            if (IsPending) _promptShown = true;
        }

        /// <summary>The player chose to join. Carried out on the next <see cref="Tick"/>.</summary>
        public static void Accept()
        {
            if (IsPending && _decision == Outcome.None) _decision = Outcome.Accepted;
        }

        /// <summary>The player chose not to join, or the decision can no longer be taken.</summary>
        public static void Cancel(string reason)
        {
            if (!IsPending || _decision != Outcome.None) return;
            _decision = Outcome.Cancelled;
            _cancelReason = reason ?? "";
        }

        /// <summary>Forget any pending decision without telling anyone. The accepted decisions stay.</summary>
        public static void Clear()
        {
            _owner = null;
            _key = "";
            PendingComparison = null;
            PendingHostName = "";
            _promptShown = false;
            _goneSince = -1f;
            _decision = Outcome.None;
            _cancelReason = "";
        }

        /// <summary>
        /// Once per frame, outside the network poll. Notices a prompt that never appeared or was closed, and hands a
        /// taken decision to <paramref name="deliver"/> (owner, outcome, cancel reason) after the pending state is
        /// cleared, so whatever the owner does next is no longer held back by it.
        /// </summary>
        public static void Tick(float now, object? runningService, Action<object, Outcome, string> deliver)
        {
            if (!IsPending) return;

            if (!ReferenceEquals(_owner, runningService)) { Clear(); return; } // its service is gone

            if (_decision == Outcome.None)
            {
                bool open = CanPrompt;
                if (now - _since > DecisionSeconds) Cancel("timeout");
                else if (!_promptShown)
                {
                    if (now - _since > PromptAppearSeconds) Cancel("prompt-not-shown");
                }
                else if (open) _goneSince = -1f;
                else if (_goneSince < 0f) _goneSince = now;
                else if (now - _goneSince > PromptGoneSeconds) Cancel("prompt-closed");
            }
            if (_decision == Outcome.None) return;

            object owner = _owner!;
            Outcome outcome = _decision;
            string reason = _cancelReason;
            if (outcome == Outcome.Accepted) _accepted.Add(_key);
            Clear();
            LastOutcome = outcome;
            deliver(owner, outcome, reason);
        }

        // The token cannot contain '\n' (it is validated to the id character set), so the two parts cannot blend.
        private static string Key(string roomToken, SessionContentComparison comparison)
            => roomToken + "\n" + comparison.Fingerprint;
    }
}
