using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2b — the FF14 force-seal barrier for OUT-OF-ROOM players (anti-cheat).
    /// <para>The vanilla combat-room door is single-sided (invisible + walk-through from the outside — see the LD-1b
    /// finding), so simply mirroring it does NOT stop an out-of-room player from shooting/walking in. When the host
    /// orders a non-in-room end to seal (t0+5 s), this spawns an INVISIBLE, SOLID, two-way barrier at that end's local
    /// door — a plain <see cref="BoxCollider"/> (solid colliders block from both sides, unlike the door's single-sided
    /// mesh), sized to the door and put on the door's collision layer so it blocks movement AND projectiles equally on
    /// both sides. It is removed when the player is let in (confirm / boss death / scene change).</para>
    /// <para>The door is located on the SEALING end by reusing the seal trigger (matched by the host-sent arena world
    /// position): the trigger's <c>onTriggerEvents</c> either closes a <c>MetalGate</c> (LD-1) or SetActives a
    /// door-named GameObject (LD-1b). Either target's renderer bounds give the barrier its placement + size.</para>
    /// </summary>
    internal static class ArenaBarrierManager
    {
        // Active barriers per arena key (one arena can have several door anchors → several barrier objects).
        private static readonly Dictionary<string, List<GameObject>> _barriers = new Dictionary<string, List<GameObject>>();

        private const float MatchEpsilon = 1.0f;   // trigger match radius (deterministic generation; absorbs drift)
        private const float Margin       = 0.6f;    // expand each door-bounds extent so the opening is fully sealed
        private const float MinThickness = 1.5f;    // enforce a minimum size on every axis (thin doors still block)
        private const float FallbackSize = 6.0f;    // box edge when a door has no renderer to measure

        private static Type _triggerType;
        private static bool _typeResolved;
        private static FieldInfo _eventField;

        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogArenaLockdown.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- seal / unseal

        /// <summary>Spawn the invisible two-way barrier(s) at the local door(s) for this arena. Idempotent.</summary>
        public static void Seal(Vector3 arenaPos)
        {
            try
            {
                string key = Key(arenaPos);
                if (_barriers.ContainsKey(key)) return; // already sealed

                object trigger = FindMatchTrigger(arenaPos);
                if (trigger == null)
                {
                    if (LogOn) NetLogger.Info($"[ArenaLockdown] SEAL no seal trigger near {arenaPos} — cannot place barrier");
                    return;
                }

                var anchors = ResolveDoorAnchors(trigger);
                if (anchors.Count == 0)
                {
                    if (LogOn) NetLogger.Info($"[ArenaLockdown] SEAL trigger has no door anchor near {arenaPos}");
                    return;
                }

                var list = new List<GameObject>();
                foreach (var a in anchors)
                {
                    var go = BuildBarrier(a);
                    if (go != null) list.Add(go);
                }
                if (list.Count > 0) _barriers[key] = list;

                if (LogOn) NetLogger.Info($"[ArenaLockdown] SEAL arena={key} placed {list.Count} barrier(s) at {anchors.Count} door anchor(s)");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] Seal failed: {ex.Message}"); }
        }

        /// <summary>Remove the barrier(s) for this arena (player let in / scene change).</summary>
        public static void Unseal(Vector3 arenaPos) => UnsealKey(Key(arenaPos));

        private static void UnsealKey(string key)
        {
            try
            {
                if (!_barriers.TryGetValue(key, out var list)) return;
                _barriers.Remove(key);
                foreach (var go in list)
                    if (go != null) UnityEngine.Object.Destroy(go);
                if (LogOn) NetLogger.Info($"[ArenaLockdown] UNSEAL arena={key} removed {list.Count} barrier(s)");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] Unseal failed: {ex.Message}"); }
        }

        public static bool IsSealed(Vector3 arenaPos) => _barriers.ContainsKey(Key(arenaPos));

        /// <summary>LD-2d: close the arena's door(s) for real when the grace period ends, by replaying the seal trigger's
        /// own action on its door target — <c>MetalGate.Close()</c> or <c>SetActive(true)</c>. Resolved directly from the
        /// trigger's <c>onTriggerEvents</c> (the same path that finds the barrier anchor), so it works regardless of the
        /// gate registry / positions. Returns how many doors were closed.</summary>
        public static int CloseArenaDoorsLocal(Vector3 arenaPos)
        {
            int n = 0;
            try
            {
                object trigger = FindMatchTrigger(arenaPos);
                if (trigger == null) return 0;
                if (_eventField == null)
                    _eventField = trigger.GetType().GetField("onTriggerEvents", BindingFlags.Public | BindingFlags.Instance);
                if (!(_eventField?.GetValue(trigger) is UnityEventBase evt)) return 0;

                int cnt = evt.GetPersistentEventCount();
                for (int i = 0; i < cnt; i++)
                {
                    string method = evt.GetPersistentMethodName(i);
                    var target = evt.GetPersistentTarget(i);
                    if (target == null) continue;

                    if (string.Equals(method, "Close", StringComparison.Ordinal)
                        && target.GetType().Name.IndexOf("MetalGate", StringComparison.Ordinal) >= 0)
                    {
                        if (GateSyncManager.CloseGate(target)) n++;
                    }
                    // Only replay a SetActive that the trigger itself uses to CLOSE the door. Replaying the opposite
                    // (a room-exit trigger's SetActive(false)) would put a door BACK that vanilla had just removed.
                    else if (string.Equals(method, "SetActive", StringComparison.Ordinal)
                             && ResolveClosingDoor(evt, i, target) is GameObject go)
                    { if (!go.activeSelf) go.SetActive(true); n++; }
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] CloseArenaDoorsLocal failed: {ex.Message}"); }
            return n;
        }

        public static void Clear()
        {
            foreach (var kv in _barriers)
                foreach (var go in kv.Value)
                    if (go != null) UnityEngine.Object.Destroy(go);
            _barriers.Clear();
        }

        /// <summary>LD-2f: the InstanceIDs of the <c>MetalGate</c> component(s) this seal trigger drives (its
        /// <c>onTriggerEvents</c> MetalGate.Close/Open targets). Used to hold that exact gate open (grace) / closed
        /// (post-seal), because the seal trigger can be far from the gate it controls (Emperor: ~50 m apart), so a
        /// position radius does NOT identify the gate — the object identity does. The gate the trigger closes is the
        /// same object a nearby "open door" trigger opens, so blocking it by id catches the reopen too.</summary>
        public static List<int> ResolveMetalGateIds(object trigger)
        {
            var ids = new List<int>();
            try
            {
                if (trigger == null) return ids;
                if (_eventField == null)
                    _eventField = trigger.GetType().GetField("onTriggerEvents", BindingFlags.Public | BindingFlags.Instance);
                if (!(_eventField?.GetValue(trigger) is UnityEventBase evt)) return ids;
                int n = evt.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    string method = evt.GetPersistentMethodName(i);
                    var target = evt.GetPersistentTarget(i);
                    if (target == null) continue;
                    if ((string.Equals(method, "Close", StringComparison.Ordinal) || string.Equals(method, "Open", StringComparison.Ordinal))
                        && target.GetType().Name.IndexOf("MetalGate", StringComparison.Ordinal) >= 0)
                        ids.Add(target.GetInstanceID());
                }
            }
            catch { }
            return ids;
        }

        /// <summary>LD-2f: resolve the MetalGate id(s) for the arena by finding its seal trigger first.</summary>
        public static List<int> ResolveMetalGateIdsFromArena(Vector3 arenaPos)
            => ResolveMetalGateIds(FindMatchTrigger(arenaPos));

        // ----------------------------------------------------------------- barrier construction

        private static GameObject BuildBarrier((Transform t, Bounds bounds, bool hasBounds, int layer) anchor)
        {
            Vector3 center;
            Vector3 size;
            if (anchor.hasBounds)
            {
                center = anchor.bounds.center;
                size   = anchor.bounds.size + new Vector3(Margin, Margin, Margin) * 2f;
            }
            else
            {
                center = anchor.t != null ? anchor.t.position : Vector3.zero;
                size   = new Vector3(FallbackSize, FallbackSize, FallbackSize);
            }
            size = new Vector3(
                Mathf.Max(size.x, MinThickness),
                Mathf.Max(size.y, MinThickness),
                Mathf.Max(size.z, MinThickness));

            var go = new GameObject("LD2_ArenaBarrier");
            go.transform.position = center;
            // Axis-aligned (renderer bounds are world-AABB); identity rotation matches.
            go.transform.rotation = Quaternion.identity;
            go.layer = anchor.layer;

            var bc = go.AddComponent<BoxCollider>();
            bc.size = size;       // local size on an unscaled transform == world size
            bc.isTrigger = false; // SOLID — blocks both sides (unlike the door's single-sided mesh)
            return go;
        }

        // ----------------------------------------------------------------- LD-Crypt: SetActive argument discrimination

        // UnityEvent exposes a persistent call's TARGET and METHOD NAME publicly, but NOT its inspector-configured
        // argument. Without that argument `SetActive` is ambiguous: an arena seal calls SetActive(TRUE) to put its door
        // in place, while a room-EXIT trigger calls SetActive(FALSE) to take a door away. Matching on the method name
        // alone made the desert crypt's LeaveTrigger (persistent = [TeleportPlayer.DoTeleport, GameObject.SetActive,
        // GameObject.SetActive, FogChangeTrigger.Trigger, SoundscapeTrigger.Trigger], door target set to OFF) look like
        // a seal: the lockdown started, and 5 s later CloseArenaDoorsLocal forced that door back ON — on every end,
        // with no release path (a SetActive door has no MetalGate, so the LD-2c gate-reopen signal never arrives).
        // Whoever had not yet stepped through was shut in for the rest of the level. Log521.
        //
        // The argument lives in the serialized call group, so it is read reflectively (fields verified against
        // UnityEngine.CoreModule): UnityEventBase.m_PersistentCalls : PersistentCallGroup → m_Calls :
        // List<PersistentCall> (indexed exactly like the public GetPersistent* accessors) → m_Arguments : ArgumentCache
        // → m_BoolArgument.
        private static FieldInfo? _fPersistentCalls, _fCalls, _fArguments, _fBoolArgument;
        private static bool _argReflectResolved, _argReflectOk;

        /// <summary>LD-Crypt: the door that persistent call <paramref name="index"/> CLOSES — non-null only for a
        /// <c>SetActive(true)</c> on a door-named GameObject. A <c>SetActive(false)</c> on the same object OPENS it and
        /// must never be treated as a seal. Returns null when the argument cannot be read: not sealing degrades an
        /// arena to vanilla behaviour, whereas forcing a door shut can lock a player in with no way out.</summary>
        internal static GameObject? ResolveClosingDoor(UnityEventBase evt, int index, UnityEngine.Object target)
        {
            try
            {
                GameObject? door = target as GameObject ?? (target as Component)?.gameObject;
                if (door == null || door.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) < 0) return null;
                if (TryGetPersistentBoolArg(evt, index, out bool activate) && activate) return door;

                if (LogOn) NetLogger.Info($"[ArenaLockdown] door-named SetActive on '{door.name}' does not CLOSE it "
                                        + "— not a seal (a room-exit trigger opens its door)");
                return null;
            }
            catch { return null; }
        }

        private static bool TryGetPersistentBoolArg(UnityEventBase evt, int index, out bool value)
        {
            value = false;
            try
            {
                if (evt == null || index < 0 || !ResolveArgReflection()) return false;
                object? group = _fPersistentCalls!.GetValue(evt);
                if (group == null) return false;
                if (!(_fCalls!.GetValue(group) is System.Collections.IList calls) || index >= calls.Count) return false;
                object? call = calls[index];
                if (call == null) return false;
                object? args = _fArguments!.GetValue(call);
                if (args == null) return false;
                if (!(_fBoolArgument!.GetValue(args) is bool b)) return false;
                value = b;
                return true;
            }
            catch { return false; }
        }

        private static bool ResolveArgReflection()
        {
            if (_argReflectResolved) return _argReflectOk;
            _argReflectResolved = true;
            try
            {
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _fPersistentCalls = typeof(UnityEventBase).GetField("m_PersistentCalls", BF);
                _fCalls = _fPersistentCalls?.FieldType.GetField("m_Calls", BF);
                Type? callType = _fCalls != null && _fCalls.FieldType.IsGenericType
                    ? _fCalls.FieldType.GetGenericArguments()[0] : null;
                _fArguments = callType?.GetField("m_Arguments", BF);
                _fBoolArgument = _fArguments?.FieldType.GetField("m_BoolArgument", BF);
                _argReflectOk = _fPersistentCalls != null && _fCalls != null && _fArguments != null
                             && _fBoolArgument != null && _fBoolArgument.FieldType == typeof(bool);
            }
            catch { _argReflectOk = false; }

            if (!_argReflectOk)
                NetLogger.Warn("[ArenaLockdown] UnityEvent persistent-argument reflection unresolved — SetActive-door "
                             + "arenas will not be sealed by the mod (MetalGate arenas are unaffected).");
            return _argReflectOk;
        }

        // ----------------------------------------------------------------- door resolution (from the seal trigger)

        /// <summary>Door anchor(s) the trigger seals: a MetalGate (LD-1 Close) or a door-named GameObject (LD-1b
        /// SetActive). Returns each anchor's transform + renderer world-bounds + collision layer.</summary>
        private static List<(Transform t, Bounds bounds, bool hasBounds, int layer)> ResolveDoorAnchors(object trigger)
        {
            var list = new List<(Transform, Bounds, bool, int)>();
            try
            {
                if (_eventField == null)
                    _eventField = trigger.GetType().GetField("onTriggerEvents", BindingFlags.Public | BindingFlags.Instance);
                if (!(_eventField?.GetValue(trigger) is UnityEventBase evt)) return list;

                int n = evt.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    string method = evt.GetPersistentMethodName(i);
                    var target = evt.GetPersistentTarget(i);
                    if (target == null) continue;

                    // LD-1 MetalGate.Close → the gate component is the door.
                    if (string.Equals(method, "Close", StringComparison.Ordinal)
                        && target.GetType().Name.IndexOf("MetalGate", StringComparison.Ordinal) >= 0)
                    {
                        var gate = (target as Component)?.gameObject;
                        if (gate != null) AddAnchor(list, gate);
                        continue;
                    }

                    // LD-1b GameObject.SetActive(true, "...door...") → that GameObject is the door. A SetActive(FALSE)
                    // on a door is the trigger REMOVING one (a room exit), never a seal — see IsDoorClosingSetActive.
                    if (string.Equals(method, "SetActive", StringComparison.Ordinal)
                        && ResolveClosingDoor(evt, i, target) is GameObject go)
                        AddAnchor(list, go);
                }
            }
            catch { }
            return list;
        }

        private static void AddAnchor(List<(Transform, Bounds, bool, int)> list, GameObject door)
        {
            Bounds b = default;
            bool has = false;
            try
            {
                var renderers = door.GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (!has) { b = r.bounds; has = true; }
                    else b.Encapsulate(r.bounds);
                }
            }
            catch { }
            list.Add((door.transform, b, has, door.layer));
        }

        // ----------------------------------------------------------------- trigger lookup (shared with LD-1b pattern)

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

        private static string Key(Vector3 p)
            => $"{Mathf.RoundToInt(p.x)}_{Mathf.RoundToInt(p.y)}_{Mathf.RoundToInt(p.z)}";
    }
}
