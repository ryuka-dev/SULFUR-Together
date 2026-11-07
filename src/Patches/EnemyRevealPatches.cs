using System;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// HR-8: <c>HellShrewSnipa.SetHidden</c> prefix / postfix. On the host, the call that discovers the shrew
    /// (<c>HasBeenDiscovered</c> false → true) is broadcast so every client reveals its copy; the client applies it with
    /// the same method (<see cref="EnemyVisualStateSync"/>). No-op anywhere else.
    /// </summary>
    internal static class EnemyRevealPatches
    {
        private static PropertyInfo? _discovered;
        private static FieldInfo? _npc;

        public static void Apply(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.HellShrewSnipa");
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "SetHidden", new[] { typeof(bool) });
                _discovered = t == null ? null : AccessTools.Property(t, "HasBeenDiscovered");
                _npc = t == null ? null : AccessTools.Field(t, "Npc");
                if (m == null || _discovered == null || _npc == null)
                {
                    Plugin.Log.Warn("[EnemyReveal] HellShrewSnipa.SetHidden / HasBeenDiscovered / Npc not found — a revealed Snipa stays hidden on clients");
                    return;
                }
                harmony.Patch(m,
                    prefix:  new HarmonyMethod(typeof(EnemyRevealPatches).GetMethod(nameof(SetHidden_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(EnemyRevealPatches).GetMethod(nameof(SetHidden_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[EnemyReveal] Patched HellShrewSnipa.SetHidden (host broadcasts the reveal).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[EnemyReveal] Apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void SetHidden_Pre(object __instance, out bool __state)
        {
            __state = false;
            if (NetConfig.GetMode() != NetMode.Host) return;
            try { __state = _discovered!.GetValue(__instance) is bool d && d; } catch { __state = true; }
        }

        private static void SetHidden_Post(object __instance, bool __state)
        {
            if (NetConfig.GetMode() != NetMode.Host || __state) return;
            try
            {
                if (!(_discovered!.GetValue(__instance) is bool d) || !d) return;
                var npc = _npc!.GetValue(__instance);
                if (npc != null) EnemyVisualStateSync.OnHostRevealed(npc);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[EnemyReveal] host capture failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
