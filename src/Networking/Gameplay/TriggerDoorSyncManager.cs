using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-1b — combat-room door sync for the <c>GameObject.SetActive</c> variant (Lucia, etc.).
    /// <para>A <c>PlayerTrigger</c> the entering player crosses fires <c>GameObject.SetActive(Doors, true)</c> via its
    /// <c>onTriggerEvents</c>. We postfix <c>PlayerTrigger.Trigger</c>, scan that UnityEvent for door-named
    /// <c>SetActive</c> targets, and broadcast their post-trigger state keyed by the trigger's world position. The
    /// receiver finds the matching local trigger and reads ITS OWN event to get its local door reference (a serialized
    /// persistent target, returned even while the door is inactive), then SetActives it.</para>
    /// <para><c>PlayerTrigger</c> lives in the Core assembly but is reached by reflection (consistent with the other
    /// world patches); <c>UnityEvent</c> is read via the public <see cref="UnityEventBase"/> persistent-call API.</para>
    /// </summary>
    internal static class TriggerDoorSyncManager
    {
        private static int _seq;
        private const float MatchEpsilon = 1.0f;

        private static Type? _triggerType;
        private static bool _typeResolved;
        private static FieldInfo? _eventField;

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableTriggerDoorSync.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogTriggerDoorSync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- capture (PlayerTrigger.Trigger postfix)

        public static void CaptureLocalTrigger(object trigger)
        {
            try
            {
                if (!Enabled) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;
                if (!(trigger is Component c) || c == null) return;

                var targets = ExtractDoorTargets(trigger);
                if (targets.Count == 0) return; // ordinary trigger with no door SetActive — nothing to sync

                var msg = new NetTriggerDoors { Sequence = ++_seq, TriggerPos = c.transform.position };
                foreach (var t in targets)
                    msg.Doors.Add(new NetTriggerDoors.DoorEntry { Name = t.go.name, Active = t.go.activeSelf });

                NetGameplaySyncBridge.ReportLocalTriggerDoors(msg);

                if (LogOn)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var d in msg.Doors) sb.Append(d.Name).Append('=').Append(d.Active ? "on" : "off").Append(' ');
                    NetLogger.Info($"[DoorSync] capture trigger={c.name} pos={c.transform.position} doors=[{sb.ToString().TrimEnd()}]");
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[DoorSync] capture failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- mirror (receiving peer)

        public static void ApplyRemote(NetTriggerDoors m)
        {
            try
            {
                if (!Enabled || m == null) return;

                object trigger = FindMatchTrigger(m.TriggerPos);
                if (trigger == null)
                {
                    if (LogOn) NetLogger.Info($"[DoorSync] mirror peer={m.PeerId} no trigger near {m.TriggerPos}");
                    return;
                }

                var targets = ExtractDoorTargets(trigger);
                int applied = 0;
                foreach (var entry in m.Doors)
                {
                    foreach (var t in targets)
                    {
                        if (!string.Equals(t.go.name, entry.Name, StringComparison.Ordinal)) continue;
                        if (t.go.activeSelf != entry.Active) t.go.SetActive(entry.Active);
                        applied++;
                        break;
                    }
                }

                if (LogOn) NetLogger.Info($"[DoorSync] mirror peer={m.PeerId} trigger near {m.TriggerPos} applied={applied}/{m.Doors.Count}");
            }
            catch (Exception ex) { NetLogger.Warn($"[DoorSync] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        /// <summary>Read a PlayerTrigger's onTriggerEvents persistent calls and return every door-named GameObject that a
        /// <c>SetActive</c> call targets (the door reference, returned even while inactive).</summary>
        private static List<(GameObject go, string name)> ExtractDoorTargets(object trigger)
        {
            var list = new List<(GameObject, string)>();
            try
            {
                if (_eventField == null)
                    _eventField = trigger.GetType().GetField("onTriggerEvents", BindingFlags.Public | BindingFlags.Instance);
                if (!(_eventField?.GetValue(trigger) is UnityEventBase evt)) return list;

                int n = evt.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    if (!string.Equals(evt.GetPersistentMethodName(i), "SetActive", StringComparison.Ordinal)) continue;
                    var target = evt.GetPersistentTarget(i);
                    GameObject go = target as GameObject ?? (target as Component)?.gameObject;
                    if (go == null) continue;
                    if (go.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) < 0) continue; // doors only
                    list.Add((go, go.name));
                }
            }
            catch { }
            return list;
        }

        private static object FindMatchTrigger(Vector3 key)
        {
            try
            {
                var t = ResolveTriggerType();
                if (t == null) return null;
                var all = UnityEngine.Object.FindObjectsOfType(t);
                Component best = null;
                float bestSqr = MatchEpsilon * MatchEpsilon;
                foreach (var o in all)
                {
                    if (!(o is Component c) || c == null) continue;
                    float sqr = (c.transform.position - key).sqrMagnitude;
                    if (sqr <= bestSqr) { bestSqr = sqr; best = c; }
                }
                return best;
            }
            catch { return null; }
        }

        private static Type ResolveTriggerType()
        {
            if (!_typeResolved)
            {
                _typeResolved = true;
                _triggerType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.PlayerTrigger")
                            ?? HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.PlayerTrigger");
            }
            return _triggerType;
        }
    }
}
