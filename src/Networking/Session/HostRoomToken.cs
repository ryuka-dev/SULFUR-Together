using System;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// What a host presents so that a client can tell "the room I already decided about" from any other room. One
    /// random value per game process, sent to every client the host accepts.
    ///
    /// The client validates it in the handshake; the consent step that follows keys its remembered decisions on it.
    ///
    /// It names a room and nothing else. It is not the host's identity, it is not a secret, and it authenticates
    /// nobody: a client only uses it, together with the exact set of content differences, to avoid asking its player
    /// the same question again after a reconnect. Someone who repeats another room's token gains at most that the
    /// player is not asked again about differences identical to ones they already accepted.
    ///
    /// Per process and not per network start: a host that renames itself or changes its port restarts its networking,
    /// and its content cannot have changed without restarting the game.
    /// </summary>
    internal static class HostRoomToken
    {
        private static string? _value;

        public static string Value => _value ??= Guid.NewGuid().ToString("N");

        /// <summary>Whether a token received from a host has the shape of one: 16 to 64 characters of the id set.</summary>
        public static bool IsValid(string? token)
        {
            if (token == null || token.Length < 16 || token.Length > 64) return false;
            foreach (char c in token)
                if (!SessionRequirementLimits.IsIdChar(c)) return false;
            return true;
        }
    }
}
