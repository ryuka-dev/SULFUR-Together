namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 2.2 peer/session metadata only.
    /// This is NOT a gameplay player and never references Unit/Player/Npc objects.
    /// </summary>
    public sealed class NetPeerSession
    {
        public string             PeerId          { get; set; } = "";
        public string             PlayerName      { get; set; } = "";
        public string             ModVersion      { get; set; } = "";
        public string             EndPoint        { get; set; } = "";
        public int                Slot            { get; set; } = -1;
        public NetPeerRole        Role            { get; set; }
        public NetConnectionState State           { get; set; }
        public float              JoinedAt        { get; set; }
        public float              LastSeen        { get; set; }
        public bool               IsLocal         { get; set; }

        public bool IsConnected => State == NetConnectionState.Connected;

        public string ToCompactString()
        {
            string local = IsLocal ? ",local" : "";
            return $"{PlayerName}(id={PeerId},slot={Slot},role={Role},state={State}{local})";
        }
    }
}
