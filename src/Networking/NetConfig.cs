using System;

namespace SULFURTogether.Networking
{
    internal static class NetConfig
    {
        public static NetMode GetMode()
        {
            if (!Plugin.Cfg.EnableNetworking.Value) return NetMode.Off;
            return Enum.TryParse<NetMode>(Plugin.Cfg.NetworkMode.Value, ignoreCase: true, out var m)
                ? m
                : NetMode.Off;
        }
    }
}
