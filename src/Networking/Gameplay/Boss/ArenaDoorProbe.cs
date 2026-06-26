using System;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase PF-0 (arena lockdown evidence): read-only diagnostic over the vanilla room-seal primitives
    /// (<c>DoorBlocker.CloseDoor</c>, <c>DoorBlockerTrigger.OnTriggerEnter</c>, <c>AllDeadTrigger</c>). Gated by
    /// <c>LogBossPreFight</c>. Its only job is to reveal, in a real co-op Cousin test, whether the boss room uses
    /// these primitives and how host vs client timing differs — so the FF14-style "NetBossArenaLockdown" design
    /// (Docs/BossPreFightFlow.md §3b) is grounded in evidence rather than guessed. It never seals/opens anything.
    /// </summary>
    internal static class ArenaDoorProbe
    {
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogBossPreFight.Value; } catch { return false; } }
        }

        private static NetMode Mode
        {
            get { try { return NetGameplaySyncBridge.BossMode; } catch { return NetMode.Off; } }
        }

        public static void OnDoorClose(object doorBlocker)
        {
            if (!LogOn || doorBlocker == null) return;
            try
            {
                string pos = "?";
                if (doorBlocker is Component c && c != null) pos = c.transform.position.ToString("F1");
                Plugin.Log.Info($"[ArenaDoor] DoorBlocker.CloseDoor mode={Mode} name={SafeName(doorBlocker)} pos={pos}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ArenaDoor] door-close log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void OnTriggerEnter(object trigger)
        {
            if (!LogOn || trigger == null) return;
            try
            {
                string pos = "?";
                if (trigger is Component c && c != null) pos = c.transform.position.ToString("F1");
                Plugin.Log.Info($"[ArenaDoor] DoorBlockerTrigger.Enter mode={Mode} name={SafeName(trigger)} pos={pos}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ArenaDoor] trigger-enter log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void OnAllDead(object allDeadTrigger, string method)
        {
            if (!LogOn || allDeadTrigger == null) return;
            try
            {
                Plugin.Log.Info($"[ArenaDoor] AllDeadTrigger.{method} mode={Mode} name={SafeName(allDeadTrigger)}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ArenaDoor] all-dead log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string SafeName(object o)
        {
            try { return o is UnityEngine.Object uo && uo != null ? uo.name : o.GetType().Name; }
            catch { return "?"; }
        }
    }
}
