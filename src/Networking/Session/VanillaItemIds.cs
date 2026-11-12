using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using PerfectRandom.Sulfur.Core;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// The highest item id the game itself defines on this build. An id claim is honoured only above it, so nothing a
    /// mod or a peer declares can make a vanilla item be treated as mod content.
    ///
    /// The source is the game's compiled <c>ItemIds</c> table, which no mod can extend. The size of the item database
    /// is not used: other mods append to that list, which would raise the ceiling over the very ids that need
    /// protecting. When the table cannot be read the ceiling is 0. A ceiling that is too low only lets more ids be
    /// withheld between two ends whose content differs; one that is too high would let a claimed id through untreated.
    /// </summary>
    internal static class VanillaItemIds
    {
        /// <summary>A table maximum at or past this is not believed: the first id any content library uses is far
        /// above the game's own, and a ceiling this high would void every claim.</summary>
        private const int SaneCeilingLimit = 32767;

        private static int _ceiling = -1; // -1 = not read yet
        private static bool _failureReported;

        /// <summary>Read once and then constant for the process. Never throws.</summary>
        public static int Ceiling
        {
            get
            {
                if (_ceiling < 0)
                {
                    // The read lives in its own method so that a missing game type fails inside this try and not in
                    // the caller, which can be another mod's Awake.
                    try { _ceiling = ReadTable(); }
                    catch (Exception) { _ceiling = 0; }
                }
                if (_ceiling == 0 && !_failureReported && SessionRequirementLog.Ready)
                {
                    _failureReported = true;
                    SessionRequirementLog.Error("the game's ItemIds table gave no usable maximum; no item id is treated as the game's own when ids are withheld.");
                }
                return _ceiling;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadTable()
        {
            int max = 0;
            foreach (FieldInfo f in typeof(ItemIds).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (f.FieldType != typeof(ItemId)) continue;
                int v = ((ItemId)f.GetValue(null)).value;
                if (v > max) max = v;
            }
            return max > 0 && max <= SaneCeilingLimit ? max : 0;
        }
    }

    /// <summary>
    /// Log output for the session requirements. A mod may register before SULFUR Together has started; until then the
    /// plugin's logger does not exist and the message goes to Unity's log, so the reason for a refusal is never lost.
    /// </summary>
    internal static class SessionRequirementLog
    {
        private const string Tag = "[SessionReq] ";

        /// <summary>The plugin's own logger is available.</summary>
        public static bool Ready => Plugin.Log != null;

        public static void Info(string message)
        {
            if (Plugin.Log != null) Plugin.Log.Info(Tag + message);
            else UnityEngine.Debug.Log("[SULFUR Together] " + Tag + message);
        }

        public static void Warn(string message)
        {
            if (Plugin.Log != null) Plugin.Log.Warn(Tag + message);
            else UnityEngine.Debug.LogWarning("[SULFUR Together] " + Tag + message);
        }

        public static void Error(string message)
        {
            if (Plugin.Log != null) Plugin.Log.Error(Tag + message);
            else UnityEngine.Debug.LogError("[SULFUR Together] " + Tag + message);
        }
    }
}
