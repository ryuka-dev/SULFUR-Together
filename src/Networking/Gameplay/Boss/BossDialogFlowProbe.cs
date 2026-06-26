using System;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase PF (FF14 dialog-sync design evidence): read-only probe over the dialog-open chokepoints so a real co-op
    /// Cousin test reveals HOW the boss dialog/fight is actually wired — whether the fight starts via a trigger volume
    /// that calls the boss start directly (no dialog), or via the boss dialog's Attack (双剑) option, or both. Gated by
    /// <c>LogBossPreFight</c>. Correlate the timestamps of these lines with the existing
    /// <c>[BossLifecycle] CousinHelper.Trigger/Introduction</c> lines to pin down the flow before wiring the sync.
    /// Never opens/closes/changes anything.
    /// </summary>
    internal static class BossDialogFlowProbe
    {
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogBossPreFight.Value; } catch { return false; } }
        }

        private static NetMode Mode
        {
            get { try { return NetGameplaySyncBridge.BossMode; } catch { return NetMode.Off; } }
        }

        /// <summary>Postfix on Npc.Interact(Player): logs whether a BOSS dialog was opened this way and its state.</summary>
        public static void OnNpcInteract(object npc, bool ran)
        {
            if (!LogOn || !ran || npc == null) return;
            try
            {
                if (!(npc is Npc n)) return;
                bool isBoss = false;
                try { isBoss = n.UnitType == PerfectRandom.Sulfur.Core.Utilities.UnitType.Boss; } catch { }
                // Only log boss interactions (avoid spamming on vendors/NPCs).
                if (!isBoss) return;
                bool hasDialog = false; try { hasDialog = n.HasDialog; } catch { }
                Plugin.Log.Info($"[DialogFlow] Npc.Interact mode={Mode} npc={SafeName(npc)} boss=True hasDialog={hasDialog}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogFlow] Npc.Interact log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Postfix on DialogController.SetCurrentSpeakable: logs dialog open (speakable!=null) / close (null).</summary>
        public static void OnSetCurrentSpeakable(object speakable)
        {
            if (!LogOn) return;
            try
            {
                string name = speakable == null ? "NULL(close)" : SafeName(speakable);
                Plugin.Log.Info($"[DialogFlow] SetCurrentSpeakable mode={Mode} -> {name}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogFlow] SetCurrentSpeakable log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Postfix on PlayerTrigger.Trigger(GameObject): identifies room-entry / close-room triggers and what
        /// objects they fire (helps map the room-membership trigger for the FF14 lockdown). Reflection (private fields).</summary>
        public static void OnPlayerTrigger(object pt)
        {
            if (!LogOn || pt == null) return;
            try
            {
                string pos = (pt is Component c && c != null) ? c.transform.position.ToString("F1") : "?";
                string evt = ""; var ev = pt.GetType().GetField("eventReference", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (ev != null) evt = ev.GetValue(pt)?.ToString() ?? "";
                string objs = ReadTriggeredObjects(pt);
                Plugin.Log.Info($"[DialogFlow] PlayerTrigger.Trigger mode={Mode} name={SafeName(pt)} pos={pos} event=\"{evt}\" fires=[{objs}]");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogFlow] PlayerTrigger log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string ReadTriggeredObjects(object pt)
        {
            try
            {
                var f = pt.GetType().GetField("objectsToTrigger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f == null) return "?";
                if (!(f.GetValue(pt) is System.Collections.IEnumerable list)) return "?";
                var names = new System.Collections.Generic.List<string>();
                foreach (var o in list) names.Add(SafeName(o));
                return names.Count == 0 ? "<none>" : string.Join(",", names);
            }
            catch { return "?"; }
        }

        private static string SafeName(object o)
        {
            try { return o is UnityEngine.Object uo && uo != null ? uo.name : (o?.GetType().Name ?? "?"); }
            catch { return "?"; }
        }
    }
}
