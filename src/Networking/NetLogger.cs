namespace SULFURTogether.Networking
{
    internal static class NetLogger
    {
        public static void Info(string msg)  => Plugin.Log.Info(msg);
        public static void Warn(string msg)  => Plugin.Log.Warn(msg);
        public static void Error(string msg) => Plugin.Log.Error(msg);
        public static void Debug(string msg) => Plugin.Log.Debug(msg);
    }
}
