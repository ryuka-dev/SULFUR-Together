using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    internal static class NetSessionSettingsCodec
    {
        public static void Write(NetDataWriter w, NetSessionSettingsState s)
        {
            w.Put(s.Revision);
            w.Put(s.FriendlyFire);
            w.Put(s.DeveloperMode);
            w.Put(s.SharedLoot);
        }

        public static bool TryRead(NetDataReader r, out NetSessionSettingsState result)
        {
            result = null!;
            try
            {
                var s = new NetSessionSettingsState();
                s.Revision      = r.GetInt();
                s.FriendlyFire  = r.GetBool();
                s.DeveloperMode = r.GetBool();
                s.SharedLoot    = r.GetBool();
                result = s;
                return true;
            }
            catch { return false; }
        }
    }
}
