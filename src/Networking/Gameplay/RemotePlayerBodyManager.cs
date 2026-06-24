using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.6-WS-3 remote player NPC billboard body.
    /// SULFUR players have no directional sprite art (proven by PVD/PSAS probes), so we reuse an NPC's billboard paper
    /// sprite as the remote player's body: resolve a humanoid <see cref="UnitSO"/> by name keyword, load its prefab
    /// (Addressables, via reflection so no extra assembly reference), then build a VISUAL-ONLY clone (all PerfectRandom
    /// gameplay MonoBehaviours + physics stripped, keeping renderers/Animator/BillboardSprite). The game's
    /// BillboardSpriteManager makes it face the camera like enemies. The held weapon model stays attached.
    /// </summary>
    internal static class RemotePlayerBodyManager
    {
        private static bool       _resolved;
        private static bool       _failed;
        private static object     _loadHandle;   // boxed AsyncOperationHandle<GameObject>
        private static GameObject _bodyPrefab;    // cached loaded prefab
        private static string     _resolvedName = "";

        public static string ResolvedName => _resolvedName;

        public static void Reset()
        {
            // Keep the cached prefab (Addressable stays loaded) across scenes; only clear if we want a full teardown.
        }

        /// <summary>Returns the cached body prefab once the Addressable load has completed; kicks off resolve/load lazily.</summary>
        public static bool TryGetBodyPrefab(out GameObject prefab)
        {
            prefab = _bodyPrefab;
            if (_bodyPrefab != null) return true;
            if (_failed) return false;
            EnsureResolved();
            PollLoad();
            prefab = _bodyPrefab;
            return _bodyPrefab != null;
        }

        private static void EnsureResolved()
        {
            if (_resolved || _failed) return;

            var assets = AsyncAssetLoading.Instance;
            UnitDatabase db = assets != null ? assets.unitDatabase : null;
            if (db == null) return; // not ready yet — retry next tick (do not latch)

            _resolved = true;
            try
            {
                UnitSO so = ResolveBodyUnitSO(db);
                if (so == null)
                {
                    _failed = true;
                    if (Plugin.Cfg.LogRemotePlayerBody.Value)
                        NetLogger.Warn("[RemoteBody] no humanoid UnitSO matched keywords — falling back to capsule");
                    return;
                }

                _resolvedName = SafeName(so);
                var mi = AccessTools.Method(typeof(UnitSO), "FetchAndLoadUnitLoader");
                if (mi == null) { _failed = true; NetLogger.Warn("[RemoteBody] FetchAndLoadUnitLoader not found"); return; }
                _loadHandle = mi.Invoke(so, null);

                if (Plugin.Cfg.LogRemotePlayerBody.Value)
                    NetLogger.Info($"[RemoteBody] resolved body unit='{_resolvedName}' id={so.id.value} — loading prefab...");
            }
            catch (Exception ex)
            {
                _failed = true;
                NetLogger.Warn($"[RemoteBody] resolve/load failed: {ex.Message}");
            }
        }

        private static void PollLoad()
        {
            if (_bodyPrefab != null || _loadHandle == null || _failed) return;
            try
            {
                var ht = _loadHandle.GetType();
                bool isDone = (bool)(ht.GetProperty("IsDone")?.GetValue(_loadHandle) ?? false);
                if (!isDone) return;

                var result = ht.GetProperty("Result")?.GetValue(_loadHandle) as GameObject;
                if (result == null)
                {
                    _failed = true;
                    NetLogger.Warn("[RemoteBody] prefab load completed but Result is null — falling back to capsule");
                    return;
                }
                _bodyPrefab = result;
                if (Plugin.Cfg.LogRemotePlayerBody.Value)
                    NetLogger.Info($"[RemoteBody] body prefab loaded: {result.name}");
            }
            catch (Exception ex)
            {
                _failed = true;
                NetLogger.Warn($"[RemoteBody] poll load failed: {ex.Message}");
            }
        }

        private static UnitSO ResolveBodyUnitSO(UnitDatabase db)
        {
            System.Collections.Generic.List<UnitSO> list;
            try { list = db.GetRawList(); } catch { return null; }
            if (list == null || list.Count == 0) return null;

            string raw = Plugin.Cfg.RemotePlayerBodyUnitKeyword.Value ?? "";
            var keywords = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            // First keyword (in order) that matches any unit wins, so the config expresses preference order.
            foreach (var kwRaw in keywords)
            {
                string kw = kwRaw.Trim().ToLowerInvariant();
                if (kw.Length == 0) continue;
                foreach (var so in list)
                {
                    if (so == null) continue;
                    string n = SafeName(so).ToLowerInvariant();
                    string dn = SafeDisplayName(so).ToLowerInvariant();
                    if (n.Contains(kw) || dn.Contains(kw))
                        return so;
                }
            }
            return null;
        }

        private static string SafeName(UnitSO so)
        {
            try { return so.name ?? ""; } catch { return ""; }
        }

        private static string SafeDisplayName(UnitSO so)
        {
            try { return so.displayName ?? ""; } catch { return ""; }
        }

        /// <summary>Build + attach an NPC body to every remote proxy that lacks one (once the prefab is loaded).</summary>
        public static void SyncProxyBodies(NetRemotePlayerProxyManager proxies)
        {
            if (proxies == null) return;
            if (!Plugin.Cfg.EnableRemotePlayerNpcBody.Value) return;
            if (!TryGetBodyPrefab(out _)) return; // resolving / loading / failed (capsule stays)

            proxies.ForEachProxy((peerId, proxy) =>
            {
                if (proxy == null || proxy.HasBody) return;
                GameObject body = BuildBody();
                if (body != null)
                {
                    proxy.SetBody(body);
                    if (Plugin.Cfg.LogRemotePlayerBody.Value)
                        NetLogger.Info($"[RemoteBody] attached NPC body to proxy {peerId} (unit='{_resolvedName}')");
                }
            });
        }

        /// <summary>Instantiate a VISUAL-ONLY body clone (gameplay + physics stripped). Caller owns it.</summary>
        public static GameObject BuildBody()
        {
            if (_bodyPrefab == null) return null;
            try
            {
                var container = new GameObject("RemoteBodyBuild");
                container.SetActive(false);
                GameObject inst = UnityEngine.Object.Instantiate(_bodyPrefab, container.transform);

                StripGameplay(inst);

                inst.SetActive(false);
                inst.transform.SetParent(null, worldPositionStays: false);
                UnityEngine.Object.Destroy(container);
                return inst;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.LogRemotePlayerBody.Value)
                    NetLogger.Warn($"[RemoteBody] build failed: {ex.Message}");
                return null;
            }
        }

        // Remove every PerfectRandom gameplay MonoBehaviour (Unit/Npc/AI/Hitmesh/audio/etc.) and physics, keeping the
        // visual hierarchy: renderers, Animator (drives sprite animation), and Billboard components (face the camera).
        private static void StripGameplay(GameObject inst)
        {
            try
            {
                foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    string full = mb.GetType().FullName ?? "";
                    if (full.StartsWith("PerfectRandom.Sulfur.Core.BillboardSprite") ||
                        full.StartsWith("PerfectRandom.Sulfur.Core.BillboardNpc"))
                        continue; // keep — camera-facing billboard
                    if (!full.StartsWith("PerfectRandom"))
                        continue; // keep non-game scripts (rare)
                    try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
                }

                foreach (var col in inst.GetComponentsInChildren<Collider>(true))
                    { try { UnityEngine.Object.DestroyImmediate(col); } catch { } }
                foreach (var rb in inst.GetComponentsInChildren<Rigidbody>(true))
                    { try { UnityEngine.Object.DestroyImmediate(rb); } catch { } }
                foreach (var src in inst.GetComponentsInChildren<AudioSource>(true))
                    { try { UnityEngine.Object.DestroyImmediate(src); } catch { } }
                foreach (var nav in inst.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
                    { try { UnityEngine.Object.DestroyImmediate(nav); } catch { } }

                if (Plugin.Cfg.LogRemotePlayerBody.Value)
                {
                    int sr = inst.GetComponentsInChildren<SpriteRenderer>(true).Length;
                    int mr = inst.GetComponentsInChildren<MeshRenderer>(true).Length;
                    int an = inst.GetComponentsInChildren<Animator>(true).Length;
                    int bb = inst.GetComponentsInChildren<BillboardSprite>(true).Length;
                    NetLogger.Info($"[RemoteBody] stripped body survivors: sprite={sr} mesh={mr} animator={an} billboard={bb}");
                }
            }
            catch { }
        }
    }
}
