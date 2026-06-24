using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.ReverseProbe
{
    /// <summary>
    /// Phase 5.6-WS-3 one-shot runtime discovery of the local player's visual structure.
    /// The directional billboard sprites (front/back/side, animated) the player wants to display for remote peers are NOT
    /// discoverable from the DLLs — they live inside the Player prefab's serialized hierarchy (likely in
    /// <c>Player.playerVisuals</c> or disabled third-person child objects). This probe dumps the live Player(Clone)
    /// hierarchy + every SpriteRenderer / Animator / Renderer once so we can locate the sprites and their animator.
    /// Diagnostic only — no behaviour change.
    /// </summary>
    internal static class PlayerVisualDiscoveryProbe
    {
        private static bool  _done;
        private static float _nextTryAt;

        public static void TryDumpOnce()
        {
            try
            {
                if (_done) return;
                if (!Plugin.Cfg.LogPlayerVisualDiscovery.Value) return;

                float now = Time.realtimeSinceStartup;
                if (now < _nextTryAt) return;
                _nextTryAt = now + 1f;

                Component player = ResolveLocalPlayer();
                if (player == null) return;

                _done = true;
                Dump(player);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[PVD] discovery failed: {ex.Message}");
            }
        }

        private static Component ResolveLocalPlayer()
        {
            var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
            if (gmType == null) return null;
            var gm = AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
            if (gm == null) return null;

            var ps = AccessTools.Property(gmType, "PlayerScript")?.GetValue(gm, null) as Component;
            if (ps != null) return ps;
            return AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null) as Component;
        }

        private static void Dump(Component player)
        {
            var Log = Plugin.Log;
            GameObject go = player.gameObject;

            Log.Info("==================== [PVD] PLAYER VISUAL DISCOVERY BEGIN ====================");
            Log.Info($"[PVD] root={go.name} active={go.activeSelf} layer={go.layer} playerType={player.GetType().FullName}");

            DumpPlayerVisuals(player);

            Log.Info("---- [PVD] SPRITE RENDERERS (incl inactive) ----");
            foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null) continue;
                Log.Info($"[PVD][Sprite] path={Path(sr.transform)} active={sr.gameObject.activeSelf} enabled={sr.enabled} layer={sr.gameObject.layer} " +
                         $"sprite={(sr.sprite != null ? sr.sprite.name : "<null>")} tex={(sr.sprite != null && sr.sprite.texture != null ? sr.sprite.texture.name : "<null>")} " +
                         $"mat={(sr.sharedMaterial != null ? sr.sharedMaterial.name : "<null>")}");
            }

            Log.Info("---- [PVD] ANIMATORS (incl inactive) ----");
            foreach (var an in go.GetComponentsInChildren<Animator>(true))
            {
                if (an == null) continue;
                var ctrl = an.runtimeAnimatorController;
                Log.Info($"[PVD][Animator] path={Path(an.transform)} active={an.gameObject.activeSelf} enabled={an.enabled} controller={(ctrl != null ? ctrl.name : "<null>")}");
                if (ctrl != null && ctrl.animationClips != null)
                    foreach (var c in ctrl.animationClips)
                        if (c != null) Log.Info($"[PVD][Animator]   clip={c.name}");
                try { foreach (var p in an.parameters) Log.Info($"[PVD][Animator]   param={p.name} ({p.type})"); } catch { }
            }

            Log.Info("---- [PVD] ALL RENDERERS (incl inactive) ----");
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                Log.Info($"[PVD][Renderer] type={r.GetType().Name} path={Path(r.transform)} active={r.gameObject.activeSelf} enabled={r.enabled} layer={r.gameObject.layer}");
            }

            Log.Info("---- [PVD] FULL HIERARCHY (incl inactive) ----");
            DumpHierarchy(go.transform, 0);

            Log.Info("==================== [PVD] PLAYER VISUAL DISCOVERY END ====================");
        }

        private static void DumpPlayerVisuals(Component player)
        {
            var Log = Plugin.Log;
            try
            {
                var f = AccessTools.Field(player.GetType(), "playerVisuals");
                if (f == null) { Log.Info("[PVD] playerVisuals field NOT FOUND on " + player.GetType().Name); return; }
                var arr = f.GetValue(player) as Renderer[];
                if (arr == null) { Log.Info("[PVD] playerVisuals == null"); return; }

                Log.Info($"---- [PVD] playerVisuals[{arr.Length}] ----");
                foreach (var r in arr)
                {
                    if (r == null) { Log.Info("[PVD][pv] <null>"); continue; }
                    string extra = "";
                    if (r is SpriteRenderer sr)
                        extra = $" sprite={(sr.sprite != null ? sr.sprite.name : "<null>")} tex={(sr.sprite != null && sr.sprite.texture != null ? sr.sprite.texture.name : "<null>")}";
                    Log.Info($"[PVD][pv] type={r.GetType().Name} path={Path(r.transform)} active={r.gameObject.activeSelf} enabled={r.enabled} layer={r.gameObject.layer}{extra}");
                }
            }
            catch (Exception ex) { Log.Warn($"[PVD] playerVisuals dump failed: {ex.Message}"); }
        }

        private static void DumpHierarchy(Transform t, int depth)
        {
            string indent = depth > 0 ? new string(' ', depth * 2) : "";
            var sb = new StringBuilder();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                sb.Append(c.GetType().Name);
                sb.Append(' ');
            }
            Plugin.Log.Info($"[PVD][H] {indent}{t.name} [active={t.gameObject.activeSelf} layer={t.gameObject.layer}] :: {sb}");
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), depth + 1);
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }
    }
}
