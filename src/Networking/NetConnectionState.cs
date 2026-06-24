namespace SULFURTogether.Networking
{
    public enum NetConnectionState : byte
    {
        Connecting    = 1,
        Handshaking   = 2,
        Connected     = 3,
        Disconnected  = 4,
        Rejected      = 5,
    }
}
