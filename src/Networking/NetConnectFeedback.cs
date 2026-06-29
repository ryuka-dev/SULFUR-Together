namespace SULFURTogether.Networking
{
    /// <summary>
    /// UI-3c: captures the most recent client connection feedback (in-progress / failure reason) so the connect
    /// page can surface it to the player in the menu instead of only in the log (CoopUiPlan §5). It is written
    /// asynchronously by <see cref="NetService"/> as the handshake resolves — a rejection carries the host's
    /// reason string, a failed/timed-out connect a generic message, a successful handshake clears it — and read
    /// by <see cref="UI.CoopConnectPage"/>. Static + role-agnostic: only the client path ever sets it.
    /// </summary>
    internal static class NetConnectFeedback
    {
        /// <summary>Last connection failure reason for display; empty when none / after a successful connect.</summary>
        public static string LastError { get; private set; } = "";

        /// <summary>True between a connect attempt starting and it resolving (connected or failed).</summary>
        public static bool Connecting { get; private set; }

        /// <summary>A Join was pressed: clear any stale error and enter the connecting state.</summary>
        public static void BeginAttempt()
        {
            LastError = "";
            Connecting = true;
        }

        /// <summary>Handshake accepted — clear the connecting/error state.</summary>
        public static void ReportConnected()
        {
            LastError = "";
            Connecting = false;
        }

        /// <summary>Connection rejected / failed / timed out — record the reason for the menu.</summary>
        public static void ReportError(string reason)
        {
            LastError = string.IsNullOrWhiteSpace(reason) ? "Connection failed" : reason;
            Connecting = false;
            Plugin.Log?.Info($"[CoopConn] join feedback: {LastError}");
        }

        /// <summary>Networking stopped — drop all feedback back to the neutral (off) state.</summary>
        public static void Clear()
        {
            LastError = "";
            Connecting = false;
        }
    }
}
