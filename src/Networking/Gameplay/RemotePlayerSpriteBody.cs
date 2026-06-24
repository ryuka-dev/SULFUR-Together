using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using PerfectRandom.Sulfur.Core;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.6-WS-3 remote player billboard body using the priest (Father) walk sprite sheets embedded in the DLL.
    /// The player has front + back animated sprites (8 frames each, no side sheet). This component:
    ///  - billboards the sprite to face the local camera horizontally (upright, like enemy paper sprites),
    ///  - picks front vs back content from the angle between the remote player's facing and the viewer direction
    ///    (player faces toward viewer → front; faces away → back — independent of the viewer's own facing),
    ///  - plays the 8-frame walk animation while the player is moving.
    /// Visual only.
    /// </summary>
    internal sealed class RemotePlayerSpriteBody : MonoBehaviour
    {
        private const int   FrameCount = 8;
        private const float FrameSeconds = 0.125f; // APNG delay = 125ms (8 fps)
        private const float PixelsPerUnit = 256f;  // front sheet 512px tall → ~2m

        private static Sprite[] _front;
        private static Sprite[] _back;
        private static bool     _loadTried;
        private static bool     _loadOk;

        private SpriteRenderer _sr;
        private float _facingYaw;
        private bool  _moving;       // synced INPUT-walking flag (not physics) → clean start/stop
        private float _animTimer;
        private int   _frame;
        private bool  _showingBack;

        public static bool ResourcesAvailable()
        {
            EnsureLoaded();
            return _loadOk;
        }

        /// <summary>Attach a Father sprite body to every remote proxy that lacks one (sheets must be available).</summary>
        public static void SyncProxyBodies(NetRemotePlayerProxyManager proxies)
        {
            if (proxies == null) return;
            if (!Plugin.Cfg.EnableRemotePlayerSpriteBody.Value) return;
            if (!ResourcesAvailable()) return;

            proxies.ForEachProxy((peerId, proxy) =>
            {
                if (proxy == null || proxy.HasBody) return;
                var body = Build();
                if (body != null)
                {
                    proxy.SetBody(body);
                    if (Plugin.Cfg.LogRemotePlayerBody.Value)
                        Plugin.Log.Info($"[FatherBody] attached sprite body to proxy {peerId}");
                }
            });
        }

        /// <summary>Build a ready-to-attach (inactive) body GameObject carrying this component, or null if sheets missing.</summary>
        public static GameObject Build()
        {
            if (!ResourcesAvailable()) return null;
            var go = new GameObject("FatherBody");
            go.SetActive(false);
            go.AddComponent<RemotePlayerSpriteBody>();
            return go;
        }

        public void SetState(float facingYaw, bool moving)
        {
            _facingYaw = facingYaw;
            _moving = moving;
        }

        private void Awake()
        {
            EnsureLoaded();
            _sr = gameObject.GetComponent<SpriteRenderer>();
            if (_sr == null) _sr = gameObject.AddComponent<SpriteRenderer>();
            _sr.sprite = (_front != null && _front.Length > 0) ? _front[0] : null;
            // We billboard ourselves (waist pivot + clamped pitch) in LateUpdate — no game BillboardSprite component.
        }

        private void LateUpdate()
        {
            if (!_loadOk || _sr == null) return;

            Camera cam = ResolveCamera();
            if (cam == null) return;

            // Anchor = waist (the parent holder is positioned at waist height); we billboard + layer around it.
            Transform parent = transform.parent;
            Vector3 anchor = parent != null ? parent.position : transform.position;

            Vector3 toCam = cam.transform.position - anchor;
            Vector3 flat = new Vector3(toCam.x, 0f, toCam.z);
            if (flat.sqrMagnitude < 1e-5f) flat = Vector3.forward;
            Vector3 flatNorm = flat.normalized;

            // Front vs back: the remote player's facing vs the viewer direction (independent of the viewer's facing).
            Vector3 playerForward = Quaternion.Euler(0f, _facingYaw, 0f) * Vector3.forward;
            bool showBack = Vector3.Dot(playerForward, flatNorm) < 0f; // faces away from viewer → back
            if (showBack != _showingBack)
            {
                _showingBack = showBack;
                _frame = Mathf.Clamp(_frame, 0, FrameCount - 1);
            }

            // Billboard: face the camera with pitch CLAMPED (like NPC sprites), rotating around the waist anchor.
            float pitchLimit = 25f;
            float depthBias = 0f;
            try { pitchLimit = Plugin.Cfg.RemoteBodyPitchLimit.Value; } catch { }
            try { depthBias = Plugin.Cfg.RemoteBodyDepthBias.Value; } catch { }

            float horiz = flat.magnitude;
            float maxY = horiz * Mathf.Tan(Mathf.Clamp(pitchLimit, 0f, 89f) * Mathf.Deg2Rad);
            float clampedY = Mathf.Clamp(toCam.y, -maxY, maxY);
            Vector3 lookDir = new Vector3(toCam.x, clampedY, toCam.z);
            if (lookDir.sqrMagnitude < 1e-6f) lookDir = flatNorm;
            transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);

            // Same layer as the held weapon: keep the sprite anchored at the waist (no depth nudge) so the weapon
            // intersects the flat paper sprite (half in front, half behind) and switching front/back never resizes it.
            // (depthBias defaults to 0; a non-zero value can re-enable the old back-in-front behaviour.)
            transform.position = anchor + flatNorm * (showBack ? depthBias : 0f);

            // Animate only while the player is INPUT-walking (synced flag); freeze on frame 0 the instant they stop.
            if (_moving)
            {
                _animTimer += Time.deltaTime;
                while (_animTimer >= FrameSeconds)
                {
                    _animTimer -= FrameSeconds;
                    _frame = (_frame + 1) % FrameCount;
                }
            }
            else
            {
                _frame = 0;
                _animTimer = 0f;
            }

            Sprite[] frames = _showingBack ? _back : _front;
            if (frames != null && frames.Length == FrameCount)
                _sr.sprite = frames[_frame];
        }

        private static Camera ResolveCamera()
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm != null && gm.currentCamera != null) return gm.currentCamera;
            }
            catch { }
            return Camera.main;
        }

        private static void EnsureLoaded()
        {
            if (_loadTried) return;
            _loadTried = true;
            try
            {
                _front = LoadSheet("father_front");
                _back = LoadSheet("father_back");
                _loadOk = _front != null && _back != null && _front.Length == FrameCount && _back.Length == FrameCount;
                if (!_loadOk)
                    Plugin.Log.Warn("[FatherBody] sprite sheets failed to load (embedded resources missing?)");
                else if (Plugin.Cfg.LogRemotePlayerBody.Value)
                    Plugin.Log.Info("[FatherBody] sprite sheets loaded (front/back, 8 frames each)");
            }
            catch (Exception ex)
            {
                _loadOk = false;
                Plugin.Log.Warn($"[FatherBody] load failed: {ex.Message}");
            }
        }

        // net472 lacks System.ReadOnlySpan, so the ImageConversion.LoadImage extension's method group won't compile
        // directly (one overload takes ReadOnlySpan<byte>). Invoke the (Texture2D, byte[], bool) overload via reflection.
        private static MethodInfo _loadImageMi;
        private static bool _loadImageResolved;

        private static bool LoadImageInto(Texture2D tex, byte[] bytes)
        {
            if (!_loadImageResolved)
            {
                _loadImageResolved = true;
                var t = HarmonyLib.AccessTools.TypeByName("UnityEngine.ImageConversion");
                if (t != null)
                    _loadImageMi = t.GetMethod("LoadImage", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }, null);
            }
            if (_loadImageMi == null) return false;
            object r = _loadImageMi.Invoke(null, new object[] { tex, bytes, false });
            return r is bool b && b;
        }

        private static Sprite[] LoadSheet(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) { Plugin.Log.Warn($"[FatherBody] embedded resource not found: {resourceName}"); return null; }
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                if (!LoadImageInto(tex, bytes)) { Plugin.Log.Warn($"[FatherBody] LoadImage failed: {resourceName}"); return null; }
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;

                int cellW = tex.width / FrameCount;
                int cellH = tex.height;
                var sprites = new Sprite[FrameCount];
                for (int i = 0; i < FrameCount; i++)
                {
                    var rect = new Rect(i * cellW, 0, cellW, cellH);
                    // Pivot = CENTRE (waist) so the billboard tilts around the waist like NPC sprites (not the feet).
                    sprites[i] = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), PixelsPerUnit, 0, SpriteMeshType.FullRect);
                    sprites[i].name = $"{resourceName}_{i}";
                }
                return sprites;
            }
        }
    }
}
