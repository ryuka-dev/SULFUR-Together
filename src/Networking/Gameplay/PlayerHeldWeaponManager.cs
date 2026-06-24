using System;
using System.Collections.Generic;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Weapons;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.6-WS-2 remote held weapon model.
    /// <para>Local side: polls the local player's currently held weapon + installed attachments and broadcasts a compact
    /// spec (WeaponSO ItemId + attachment ItemIds) on change (plus a low-rate heartbeat for late joiners).</para>
    /// <para>Remote side: stores each peer's desired spec, then (re)builds the weapon model — instantiating the WeaponSO's
    /// showcase/held prefab, stripping all gameplay logic, and showing the matching <c>WeaponAttachmentVisual</c> children
    /// (attachments change the model) — and attaches it to that player's proxy hands. VISUAL ONLY.</para>
    /// </summary>
    internal static class PlayerHeldWeaponManager
    {
        // ---- local poll/broadcast state ----
        private const float PollInterval = 0.25f;
        private const float HeartbeatSeconds = 3f;
        private static float _nextPollAt;
        private static float _lastSentAt;
        private static string _lastSentSig = "";

        // ---- received desired specs (peerId → latest spec) ----
        private static readonly Dictionary<string, NetPlayerHeldWeapon> _desiredByPeer = new Dictionary<string, NetPlayerHeldWeapon>();

        // ---- reflection-free type caches resolved lazily via Core types ----

        public static void Tick(NetRemotePlayerProxyManager proxies)
        {
            if (!Plugin.Cfg.EnableRemoteWeaponModel.Value) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextPollAt)
                {
                    _nextPollAt = now + PollInterval;
                    PollLocalAndBroadcast(now);
                }
                SyncRemoteModels(proxies);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.LogRemoteWeaponModel.Value)
                    NetLogger.Warn($"[HeldWeapon] tick failed: {ex.Message}");
            }
        }

        public static void Reset()
        {
            _desiredByPeer.Clear();
            _lastSentSig = "";
            _lastSentAt = 0f;
            _nextPollAt = 0f;
        }

        public static void RemovePeer(string peerId)
        {
            if (!string.IsNullOrEmpty(peerId))
                _desiredByPeer.Remove(peerId);
        }

        // ----------------------------------------------------------------- local poll → broadcast

        private static void PollLocalAndBroadcast(float now)
        {
            if (!NetGameplaySyncBridge.IsSessionActive) return;

            var msg = ReadLocalHeldWeapon();
            string sig = msg.Signature();

            if (sig == _lastSentSig && now - _lastSentAt < HeartbeatSeconds) return;

            _lastSentSig = sig;
            _lastSentAt = now;
            NetGameplaySyncBridge.ReportLocalHeldWeapon(msg);

            if (Plugin.Cfg.LogRemoteWeaponModel.Value)
                NetLogger.Info($"[HeldWeapon] broadcast hasWeapon={msg.HasWeapon} weaponId={msg.WeaponItemId} attachments={msg.AttachmentItemIds.Length} sig={sig}");
        }

        private static NetPlayerHeldWeapon ReadLocalHeldWeapon()
        {
            var msg = new NetPlayerHeldWeapon { HasWeapon = false };
            try
            {
                GameManager gm = GameManager.Instance;
                EquipmentManager em = gm != null ? gm.EquipmentManager : null;
                Holdable held = em != null ? em.CurrentHoldable : null;

                if (held is Weapon weapon && weapon.weaponDefinition != null)
                {
                    msg.HasWeapon = true;
                    msg.WeaponItemId = weapon.weaponDefinition.id.value;

                    var ids = new List<ushort>();
                    var inv = weapon.inventoryItem;
                    if (inv != null && inv.attachments != null)
                    {
                        foreach (ItemDefinition att in inv.attachments)
                        {
                            if (att == null) continue;
                            ids.Add(att.id.value);
                        }
                    }
                    msg.AttachmentItemIds = ids.ToArray();
                }
            }
            catch { /* leave HasWeapon=false */ }
            return msg;
        }

        // ----------------------------------------------------------------- remote apply + model sync

        public static void Apply(NetPlayerHeldWeapon msg)
        {
            if (!Plugin.Cfg.EnableRemoteWeaponModel.Value) return;
            if (msg == null || string.IsNullOrEmpty(msg.PeerId)) return;
            _desiredByPeer[msg.PeerId] = msg;
        }

        private static void SyncRemoteModels(NetRemotePlayerProxyManager proxies)
        {
            if (proxies == null || _desiredByPeer.Count == 0) return;

            proxies.ForEachProxy((peerId, proxy) =>
            {
                if (proxy == null) return;
                if (!_desiredByPeer.TryGetValue(peerId, out var spec)) return;

                string desiredSig = spec.Signature();
                if (proxy.HeldWeaponSig == desiredSig) return; // already matches

                if (!spec.HasWeapon)
                {
                    proxy.ClearHeldWeapon();
                    if (Plugin.Cfg.LogRemoteWeaponModel.Value)
                        NetLogger.Info($"[HeldWeapon] cleared weapon for {peerId}");
                    return;
                }

                GameObject model = BuildWeaponModel(spec);
                if (model != null)
                {
                    proxy.UpdateHeldWeapon(model, desiredSig);
                    if (Plugin.Cfg.LogRemoteWeaponModel.Value)
                        NetLogger.Info($"[HeldWeapon] built model peer={peerId} weaponId={spec.WeaponItemId} attachments={spec.AttachmentItemIds.Length}");
                }
            });
        }

        /// <summary>Build a visual-only weapon model (gameplay stripped) with the matching attachment visuals shown.</summary>
        private static GameObject BuildWeaponModel(NetPlayerHeldWeapon spec)
        {
            try
            {
                AsyncAssetLoading assets = AsyncAssetLoading.Instance;
                ItemDatabase db = assets != null ? assets.itemDatabase : null;
                if (db == null) return null;

                ItemDefinition def = db[new ItemId(spec.WeaponItemId)];
                if (def == null) return null;

                GameObject src = def.showcasePrefab != null ? def.showcasePrefab : def.prefab;
                if (src == null) return null;

                // Instantiate under an INACTIVE container so no Awake runs before we strip gameplay logic.
                var container = new GameObject("HeldWeaponBuild");
                container.SetActive(false);
                GameObject inst = UnityEngine.Object.Instantiate(src, container.transform);

                StripGameplay(inst);
                ApplyAttachments(inst, spec.AttachmentItemIds);

                // Detach from the throwaway container (kept inactive so attachment Awakes run only after the proxy attaches).
                inst.SetActive(false);
                inst.transform.SetParent(null, worldPositionStays: false);
                UnityEngine.Object.Destroy(container);
                return inst;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.LogRemoteWeaponModel.Value)
                    NetLogger.Warn($"[HeldWeapon] build failed for weaponId={spec.WeaponItemId}: {ex.Message}");
                return null;
            }
        }

        // Remove everything that drives gameplay / heavy rendering, keeping only the visual mesh hierarchy and the
        // WeaponAttachmentVisual components (needed to toggle attachment meshes). DestroyImmediate (while inactive) so
        // the stripped components' Awake never runs.
        private static void StripGameplay(GameObject inst)
        {
            try
            {
                var behaviours = inst.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
                foreach (var b in behaviours)
                {
                    if (b == null) continue;
                    if (b is WeaponAttachmentVisual) continue; // keep — drives attachment visibility
                    try { UnityEngine.Object.DestroyImmediate(b); } catch { }
                }

                foreach (var cam in inst.GetComponentsInChildren<Camera>(true))
                    { try { UnityEngine.Object.DestroyImmediate(cam); } catch { } }
                foreach (var light in inst.GetComponentsInChildren<Light>(true))
                    { try { UnityEngine.Object.DestroyImmediate(light); } catch { } }
                foreach (var rb in inst.GetComponentsInChildren<Rigidbody>(true))
                    { try { UnityEngine.Object.DestroyImmediate(rb); } catch { } }
                foreach (var col in inst.GetComponentsInChildren<Collider>(true))
                    { try { UnityEngine.Object.DestroyImmediate(col); } catch { } }
                foreach (var anim in inst.GetComponentsInChildren<Animator>(true))
                    { try { anim.enabled = false; } catch { } }
            }
            catch { }
        }

        private static void ApplyAttachments(GameObject inst, ushort[] attachmentIds)
        {
            try
            {
                var set = new HashSet<ushort>(attachmentIds ?? Array.Empty<ushort>());
                var visuals = inst.GetComponentsInChildren<WeaponAttachmentVisual>(includeInactive: true);
                foreach (var v in visuals)
                {
                    if (v == null) continue;
                    ushort aid = v.attachmentDefinition != null ? v.attachmentDefinition.id.value : (ushort)0;
                    bool installed = aid != 0 && set.Contains(aid);
                    try
                    {
                        if (installed) v.Show();
                        else v.Hide();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
