using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HZ-2 throwable-effect sync — peer-authoritative EFFECT mirror for Breakable-type throwables (poison/fire flasks,
    /// explosives, …). The thrower runs the real throw + break locally; when the thrown Breakable dies we broadcast the
    /// throwing weapon's ItemId + locked impact pose, and every other peer resolves that weapon's <c>prefabToThrow</c>,
    /// instantiates it at the spot and <c>Break()</c>s it so the on-break effect appears on every screen.
    /// <para>Identity is the WEAPON ItemId (stable + unique in the item database), not the thrown Breakable's UnitSO id —
    /// several grenades share one UnitSO id and it isn't spawnable via the unit database (Log398). Damage stays local per
    /// peer; HZ-1 stops the host double-counting a client's DoT.</para>
    /// </summary>
    internal static class ThrowableEffectManager
    {
        // Breakables tagged "thrown by ThrowableWeapon" (owner side) → the throwing weapon's ItemId value. Breakable.Die
        // routes here (not to the in-scene BreakableBreak channel, which would never match on peers).
        private static readonly Dictionary<Breakable, int> _thrown = new Dictionary<Breakable, int>();

        // True while we spawn+break a mirrored throwable — suppresses the resulting Die's capture (this channel AND the
        // BreakableBreak channel check it) so a mirror never echoes back out.
        private static bool _applyingMirror;
        public static bool IsApplyingMirror => _applyingMirror;

        private static int _captureSeq;

        private static FieldInfo? _prefabToThrowField; // ThrowableWeapon.prefabToThrow (private)
        private static FieldInfo? _preventLootField;   // Breakable.preventDroppingLoot (private) — set on mirrors, no loot dup

        // ------------------------------------------------------------------ tagging (owner side, from ThrowablePatches)

        public static void MarkThrown(Breakable b, int itemIdValue)
        {
            if (b == null) return;
            try { _thrown[b] = itemIdValue; } catch { }
        }

        public static bool IsThrown(Breakable b)
        {
            if (b == null) return false;
            try { return _thrown.ContainsKey(b); } catch { return false; }
        }

        public static void Unmark(Breakable b)
        {
            if (b == null) return;
            try { _thrown.Remove(b); } catch { }
        }

        // ------------------------------------------------------------------ capture (Breakable.Die prefix, thrown item)

        public static void CaptureLocalThrowableEffect(Breakable b)
        {
            try
            {
                if (_applyingMirror) return;
                if (!Plugin.Cfg.EnableThrowableEffectSync.Value) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;
                if (b == null) return;
                if (!_thrown.TryGetValue(b, out int itemId) || itemId == 0)
                {
                    if (Plugin.Cfg.LogThrowableEffectSync.Value)
                        NetLogger.Info($"[ThrowableEffect] capture skip (no ItemId) name={b.name}");
                    return;
                }

                Vector3 pos = b.transform.position;
                var msg = new NetThrowableEffect
                {
                    Sequence    = ++_captureSeq,
                    ItemIdValue = itemId,
                    Position    = pos,
                    RotationY   = b.transform.eulerAngles.y,
                };

                NetGameplaySyncBridge.ReportLocalThrowableEffect(msg);

                if (Plugin.Cfg.LogThrowableEffectSync.Value)
                    NetLogger.Info($"[ThrowableEffect] capture name={b.name} itemId={itemId} pos={pos}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ThrowableEffect] capture failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ mirror (receiving peer)

        public static void ApplyRemoteThrowableEffect(NetThrowableEffect m)
        {
            try
            {
                if (!Plugin.Cfg.EnableThrowableEffectSync.Value) return;
                if (m == null) return;

                GameObject? weaponPrefab = RuntimeSpawnManager.ResolveItemPrefab(m.ItemIdValue);
                var tw = weaponPrefab != null ? weaponPrefab.GetComponent<ThrowableWeapon>() : null;
                if (tw == null)
                {
                    if (Plugin.Cfg.LogThrowableEffectSync.Value)
                        NetLogger.Info($"[ThrowableEffect] mirror peer={m.PeerId} no ThrowableWeapon for itemId={m.ItemIdValue}");
                    return;
                }

                _prefabToThrowField ??= AccessTools.Field(typeof(ThrowableWeapon), "prefabToThrow");
                var prefabToThrow = _prefabToThrowField?.GetValue(tw) as GameObject;
                if (prefabToThrow == null)
                {
                    if (Plugin.Cfg.LogThrowableEffectSync.Value)
                        NetLogger.Info($"[ThrowableEffect] mirror peer={m.PeerId} itemId={m.ItemIdValue} has no prefabToThrow (projectile-path?)");
                    return;
                }

                Transform? parent = ResolveEffectRoot();
                var rot = Quaternion.Euler(0f, m.RotationY, 0f);
                var go = UnityEngine.Object.Instantiate(prefabToThrow, m.Position, rot, parent);
                var brk = go != null ? go.GetComponent<Breakable>() : null;
                if (brk == null)
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                    if (Plugin.Cfg.LogThrowableEffectSync.Value)
                        NetLogger.Info($"[ThrowableEffect] mirror peer={m.PeerId} itemId={m.ItemIdValue} prefabToThrow has no Breakable");
                    return;
                }

                // Guard so the init/break (and any child/linked cascade) never re-broadcasts on this peer.
                _applyingMirror = true;
                try
                {
                    // Reproduce the throw's init (minus the physics throw), then break in place at the locked impact spot.
                    var owner = ResolveLocalPlayerUnit();
                    if (owner != null) brk.SetOwner(owner);
                    brk.SetStats(brk.unitSO);
                    brk.Spawn();
                    TrySuppressLoot(brk);
                    brk.Break();
                }
                finally { _applyingMirror = false; }

                if (Plugin.Cfg.LogThrowableEffectSync.Value)
                    NetLogger.Info($"[ThrowableEffect] mirror peer={m.PeerId} spawned+broke itemId={m.ItemIdValue} name={brk.name} at {m.Position}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ThrowableEffect] mirror failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void TrySuppressLoot(Breakable brk)
        {
            try
            {
                _preventLootField ??= AccessTools.Field(typeof(Breakable), "preventDroppingLoot");
                _preventLootField?.SetValue(brk, true);
            }
            catch { }
        }

        private static Transform? ResolveEffectRoot()
        {
            try
            {
                var gm = RuntimeSpawnManager.GameManagerInstance();
                return gm != null ? BossReflect.GetMember(gm, "effectRoot") as Transform : null;
            }
            catch { return null; }
        }

        private static Unit? ResolveLocalPlayerUnit()
        {
            try
            {
                var gm = RuntimeSpawnManager.GameManagerInstance();
                return gm != null ? BossReflect.GetMember(gm, "PlayerUnit") as Unit : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ lifecycle

        public static void Clear()
        {
            _thrown.Clear();
        }
    }
}
