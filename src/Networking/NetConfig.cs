namespace SULFURTogether.Networking
{
    internal static class NetConfig
    {
        /// <summary>
        /// The live networking role. This is now purely a <b>runtime</b> value owned by <see cref="CoopConnection"/>
        /// (set by the connect page's Create / Join, cleared on Stop) — it is no longer read back from the .cfg, so
        /// the config file never persists "who is host / client". Returns <see cref="NetMode.Off"/> until a session
        /// is started.
        /// </summary>
        public static NetMode GetMode() => CoopConnection.CurrentMode;
    }
}
