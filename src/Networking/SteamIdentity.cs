using System;
using System.Reflection;
using HarmonyLib;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// UI-3d: reads the local player's Steam persona name so the connect page can auto-seed the "Player name" field
    /// instead of leaving it at the generic default. SULFUR ships Steamworks.NET and fetches the name itself at
    /// startup (<c>SteamworksManager.Start</c> logs "Steam initialized, playing as {name}") — we read the same source
    /// it does: the global <c>SteamManager.Initialized</c> guard + <c>Steamworks.SteamFriends.GetPersonaName()</c>.
    ///
    /// All reflection (no compile-time Steamworks reference), and a hard soft-dependency: if Steam isn't running, the
    /// types are absent, or the call throws, this returns false and the page falls back to the configured name. The
    /// SteamManager singleton is already created by the game by the time any menu is open, so reading Initialized
    /// here never triggers a second <c>SteamAPI.Init</c>.
    /// </summary>
    internal static class SteamIdentity
    {
        private static bool _resolved;
        private static PropertyInfo _initializedProp;   // SteamManager.Initialized (static bool)
        private static MethodInfo   _getPersonaName;    // Steamworks.SteamFriends.GetPersonaName() : string

        private static string _cachedName;
        private static bool   _cacheTried;

        /// <summary>The Steam persona name, resolved once and cached. False when Steam is unavailable.</summary>
        public static bool TryGetPersonaName(out string name)
        {
            if (_cacheTried) { name = _cachedName; return !string.IsNullOrWhiteSpace(_cachedName); }
            _cacheTried = true;
            _cachedName = Resolve();
            name = _cachedName;
            return !string.IsNullOrWhiteSpace(_cachedName);
        }

        private static string Resolve()
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    var steamManager = AccessTools.TypeByName("SteamManager");
                    _initializedProp = steamManager != null ? AccessTools.Property(steamManager, "Initialized") : null;
                    var steamFriends = AccessTools.TypeByName("Steamworks.SteamFriends");
                    _getPersonaName = steamFriends != null ? AccessTools.Method(steamFriends, "GetPersonaName") : null;
                }
                if (_initializedProp == null || _getPersonaName == null) return null;

                object init = _initializedProp.GetValue(null);
                if (!(init is bool ok) || !ok) return null;

                string name = _getPersonaName.Invoke(null, null) as string;
                if (!string.IsNullOrWhiteSpace(name))
                    Plugin.Log?.Info($"[SteamIdentity] persona name resolved: {name}");
                return name;
            }
            catch (Exception e)
            {
                Plugin.Log?.Info($"[SteamIdentity] persona name unavailable: {e.Message}");
                return null;
            }
        }
    }
}
