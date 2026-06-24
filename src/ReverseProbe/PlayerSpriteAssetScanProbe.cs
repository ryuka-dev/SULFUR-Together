using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.ReverseProbe
{
    /// <summary>
    /// Phase 5.6-WS-3b one-shot scan for player directional sprite art that is NOT in the Player prefab.
    /// PlayerVisualDiscoveryProbe proved the live Player(Clone) third-person body is just a Capsule — no front/back/side
    /// sprites. If such art exists at all it lives in loaded assets / Addressables not referenced by the player object.
    /// This probe enumerates every loaded Sprite / Texture2D / prefab GameObject / Animator-controller / AnimationClip
    /// and the Addressables catalog keys, filtering by player/character keywords, so we can find (and later load) it.
    /// Diagnostic only.
    /// </summary>
    internal static class PlayerSpriteAssetScanProbe
    {
        private static bool  _done;
        private static float _playerSeenAt = -1f;

        private static readonly string[] Keywords =
        {
            "player", "character", "body", "front", "back", "side", "walk", "idle", "run", "move",
            "portrait", "hero", "protag", "doll", "billboard", "third", "coop", "co-op", "ghost",
            "prisoner", "inmate", "straitjacket", "patient", "guy", "dude", "avatar"
        };

        public static void TryScanOnce()
        {
            try
            {
                if (_done) return;
                if (!Plugin.Cfg.LogPlayerSpriteAssetScan.Value) return;

                // Wait until the player exists, then a few seconds more so scene/Addressable assets have settled.
                float now = Time.realtimeSinceStartup;
                if (ResolveLocalPlayer() == null) { _playerSeenAt = -1f; return; }
                if (_playerSeenAt < 0f) { _playerSeenAt = now; return; }
                if (now - _playerSeenAt < 3f) return;

                _done = true;
                Scan();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[PSAS] scan failed: {ex.Message}");
            }
        }

        private static Component ResolveLocalPlayer()
        {
            var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
            if (gmType == null) return null;
            var gm = AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
            if (gm == null) return null;
            return AccessTools.Property(gmType, "PlayerScript")?.GetValue(gm, null) as Component
                ?? AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null) as Component;
        }

        private static bool Matches(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var k in Keywords)
                if (n.Contains(k)) return true;
            return false;
        }

        private static void Scan()
        {
            var Log = Plugin.Log;
            Log.Info("==================== [PSAS] PLAYER SPRITE ASSET SCAN BEGIN ====================");

            ScanSprites(Log);
            ScanTextures(Log);
            ScanPrefabs(Log);
            ScanAnimators(Log);
            ScanAddressables(Log);

            Log.Info("==================== [PSAS] PLAYER SPRITE ASSET SCAN END ====================");
        }

        private static void ScanSprites(Logging.STLogger Log)
        {
            var all = Resources.FindObjectsOfTypeAll<Sprite>();
            int matched = 0;
            Log.Info($"---- [PSAS] SPRITES total={all.Length} (keyword matches below) ----");
            foreach (var s in all)
            {
                if (s == null) continue;
                string tex = s.texture != null ? s.texture.name : "<null>";
                if (!Matches(s.name) && !Matches(tex)) continue;
                if (matched++ >= 400) { Log.Info("[PSAS][Sprite] ... (truncated)"); break; }
                Log.Info($"[PSAS][Sprite] name={s.name} tex={tex} rect={s.rect.width}x{s.rect.height}");
            }
            Log.Info($"---- [PSAS] SPRITES matched={matched} ----");
        }

        private static void ScanTextures(Logging.STLogger Log)
        {
            var all = Resources.FindObjectsOfTypeAll<Texture2D>();
            int matched = 0;
            Log.Info($"---- [PSAS] TEXTURES total={all.Length} (keyword matches below) ----");
            foreach (var t in all)
            {
                if (t == null || !Matches(t.name)) continue;
                if (matched++ >= 300) { Log.Info("[PSAS][Tex] ... (truncated)"); break; }
                Log.Info($"[PSAS][Tex] name={t.name} {t.width}x{t.height}");
            }
            Log.Info($"---- [PSAS] TEXTURES matched={matched} ----");
        }

        private static void ScanPrefabs(Logging.STLogger Log)
        {
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            int matched = 0;
            Log.Info($"---- [PSAS] GAMEOBJECTS total={all.Length} (keyword matches; scene=false = likely prefab/asset) ----");
            foreach (var go in all)
            {
                if (go == null || !Matches(go.name)) continue;
                // skip our own proxies and obvious noise
                if (go.name.IndexOf("SULFUR Together", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                bool hasSprite = go.GetComponentInChildren<SpriteRenderer>(true) != null;
                if (matched++ >= 300) { Log.Info("[PSAS][GO] ... (truncated)"); break; }
                Log.Info($"[PSAS][GO] name={go.name} inScene={go.scene.IsValid()} hasSpriteRenderer={hasSprite}");
            }
            Log.Info($"---- [PSAS] GAMEOBJECTS matched={matched} ----");
        }

        private static void ScanAnimators(Logging.STLogger Log)
        {
            int matched = 0;
            Log.Info("---- [PSAS] ANIMATOR CONTROLLERS / CLIPS (keyword matches) ----");
            foreach (var c in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
            {
                if (c == null || !Matches(c.name)) continue;
                if (matched++ >= 200) break;
                Log.Info($"[PSAS][Ctrl] name={c.name}");
            }
            foreach (var clip in Resources.FindObjectsOfTypeAll<AnimationClip>())
            {
                if (clip == null || !Matches(clip.name)) continue;
                if (matched++ >= 400) { Log.Info("[PSAS][Clip] ... (truncated)"); break; }
                Log.Info($"[PSAS][Clip] name={clip.name}");
            }
            Log.Info($"---- [PSAS] ANIMATORS matched={matched} ----");
        }

        // Addressables.ResourceLocators[*].Keys via reflection (no new assembly reference needed).
        private static void ScanAddressables(Logging.STLogger Log)
        {
            Log.Info("---- [PSAS] ADDRESSABLES KEYS (keyword matches) ----");
            try
            {
                var addrType = AccessTools.TypeByName("UnityEngine.AddressableAssets.Addressables");
                if (addrType == null) { Log.Info("[PSAS][Addr] Addressables type not found"); return; }

                var locatorsProp = AccessTools.Property(addrType, "ResourceLocators");
                if (locatorsProp == null) { Log.Info("[PSAS][Addr] ResourceLocators not found"); return; }

                if (!(locatorsProp.GetValue(null, null) is IEnumerable locators)) { Log.Info("[PSAS][Addr] ResourceLocators null"); return; }

                int matched = 0, total = 0;
                foreach (var loc in locators)
                {
                    if (loc == null) continue;
                    var keysProp = loc.GetType().GetProperty("Keys", BindingFlags.Public | BindingFlags.Instance);
                    if (keysProp == null) continue;
                    if (!(keysProp.GetValue(loc, null) is IEnumerable keys)) continue;

                    foreach (var key in keys)
                    {
                        if (!(key is string ks)) continue;
                        total++;
                        if (!Matches(ks)) continue;
                        if (matched++ >= 400) { Log.Info("[PSAS][Addr] ... (truncated)"); break; }
                        Log.Info($"[PSAS][Addr] key={ks}");
                    }
                    if (matched >= 400) break;
                }
                Log.Info($"---- [PSAS] ADDRESSABLES totalStringKeys={total} matched={matched} ----");
            }
            catch (Exception ex)
            {
                Log.Warn($"[PSAS][Addr] enumeration failed: {ex.Message}");
            }
        }
    }
}
