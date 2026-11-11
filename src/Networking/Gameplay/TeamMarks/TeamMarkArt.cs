using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// Team marks (issue #26): the three icons (one, two, three skulls over a small arrow), drawn at run time from the
    /// game's own portrait of the Black Guild skeleton, so no game art ships with the mod. The portrait is the unit's
    /// artwork (<c>UnitSO.artworkRef</c>), loaded through the game's own <c>UnitSO.LoadAndSetArtwork</c>.
    ///
    /// <para>The skull is cut out of the portrait's alpha only (the colours are ours): crop the head, scale it up, tilt it
    /// 6 degrees clockwise, threshold the alpha, cut the neck off, fill the eye sockets and mouth in a dark green, and
    /// keep the main piece. The icons stack copies of it: one in front, the others smaller and darker behind it, with a
    /// transparent gap around each head in front. Until the portrait is in, and if it cannot be had, the icon is the arrow
    /// alone; a portrait that arrives late still replaces it.</para>
    /// </summary>
    internal static class TeamMarkArt
    {
        private const string SkullUnitName = "BlackGuildSkeleton";
        private const float LoadTimeoutSeconds = 20f;

        // Output: 320 x 248 (the 160 x 124 design at 2x), pivot at the arrow's tip.
        private const int OutWidth = 320;
        private const int SkullAreaHeight = 200;
        private const int ArrowGap = 12, ArrowWidth = 60, ArrowHeight = 32, BottomPad = 4;
        private const int OutHeight = SkullAreaHeight + ArrowGap + ArrowHeight + BottomPad;

        private static readonly Color32 Front = new Color32(245, 166, 59, 255);   // HUD orange
        private static readonly Color32 Back1 = new Color32(190, 120, 40, 255);
        private static readonly Color32 Back2 = new Color32(140, 88, 32, 255);
        private static readonly Color32 Socket = new Color32(29, 59, 51, 255);

        private enum State { NotStarted, Loading, TimedOut, Ready }
        private static State _state;
        private static float _loadStartedAt;
        private static readonly Sprite?[] _icons = new Sprite?[3];
        private static Sprite? _arrowOnly;

        /// <summary>The icon for <paramref name="otherMarks"/> (1-3, clamped): the skulls once they are drawn, the arrow
        /// alone before that or without the portrait.</summary>
        public static Sprite? Get(int otherMarks)
        {
            if (_state == State.Ready) return _icons[Mathf.Clamp(otherMarks, 1, 3) - 1];
            if (_arrowOnly == null)
            {
                try { _arrowOnly = Compose(0, null, null, 0, 0); }
                catch (Exception ex) { Plugin.Log.Warn($"[TeamMark] arrow icon failed: {ex.Message}"); _state = State.Ready; }
            }
            return _arrowOnly;
        }

        /// <summary>Start or watch the load. Called every frame by the presenter while a session is active; cheap once
        /// the icons are ready.</summary>
        public static void Tick()
        {
            if (_state == State.Ready || _state == State.TimedOut) return;
            if (_state == State.Loading)
            {
                if (Time.realtimeSinceStartup - _loadStartedAt > LoadTimeoutSeconds)
                {
                    _state = State.TimedOut; // a late callback still draws the skulls
                    Plugin.Log.Warn($"[TeamMark] skull portrait not loaded after {LoadTimeoutSeconds:0} s — marks show the arrow only until it is");
                }
                return;
            }
            var db = AsyncAssetLoading.Instance != null ? AsyncAssetLoading.Instance.unitDatabase : null;
            if (db == null) return; // asset loading not done yet: try again next frame
            UnitSO? so = null;
            try
            {
                foreach (var u in db.GetRawList())
                    if (u != null && string.Equals(u.name, SkullUnitName, StringComparison.Ordinal)) { so = u; break; }
            }
            catch (Exception ex) { Finish(null, $"unit database: {ex.Message}"); return; }
            if (so == null) { Finish(null, $"no unit named {SkullUnitName}"); return; }

            _state = State.Loading;
            _loadStartedAt = Time.realtimeSinceStartup;
            try { so.LoadAndSetArtwork(sprite => Finish(sprite, sprite == null ? "the portrait loaded empty" : null)); }
            catch (Exception ex) { Finish(null, $"LoadAndSetArtwork: {ex.Message}"); }
        }

        private static void Finish(Sprite? portrait, string? why)
        {
            if (_state == State.Ready) return; // drawn already
            _state = State.Ready;
            float[]? skull = null, sockets = null;
            int w = 0, h = 0;
            try
            {
                if (portrait != null) CutSkull(portrait, out skull, out sockets, out w, out h);
                if (skull == null && _arrowOnly == null) _arrowOnly = Compose(0, null, null, 0, 0);
                for (int n = 1; n <= 3; n++) _icons[n - 1] = skull != null ? Compose(n, skull, sockets!, w, h) : _arrowOnly;
            }
            catch (Exception ex)
            {
                why = $"{ex.GetType().Name}: {ex.Message}";
                skull = null;
                for (int n = 1; n <= 3; n++) _icons[n - 1] = _arrowOnly;
            }
            if (skull != null)
                Plugin.Log.Info($"[TeamMark] icons drawn from {portrait!.name} (rect {portrait.rect.width}x{portrait.rect.height}, " +
                                $"textureRect {SafeTextureRect(portrait)}), skull {w}x{h}");
            else
                Plugin.Log.Warn($"[TeamMark] skull portrait unavailable ({why}) — marks show the arrow only");
        }

        // ----------------------------------------------------------------- the skull

        // The portrait is 256 x 256; the head is in (30, 0)-(200, 140) from the top left. Scaled by S for the cut-out.
        private const int S = 2;
        private const float Tilt = 6f;          // degrees, clockwise on screen
        private const float AlphaCut = 110f / 255f;

        /// <summary>Coverage masks (row 0 = top) of the skull's bone and of its filled eye sockets and mouth.</summary>
        private static void CutSkull(Sprite portrait, out float[] bone, out float[] holes, out int w, out int h)
        {
            if (portrait.packed && (portrait.packingMode == SpritePackingMode.Tight || portrait.packingRotation != SpritePackingRotation.None))
                throw new InvalidOperationException("the portrait is tightly packed or rotated in an atlas");
            float[] src = ReadAlpha(portrait, out int tw, out int th);
            // The recipe is in 256 x 256 portrait pixels of the sprite's own rect; an atlas may have trimmed the transparent
            // border, leaving the pixels at textureRectOffset inside that rect.
            float pw = portrait.rect.width, ph = portrait.rect.height;
            Vector2 off = portrait.textureRectOffset;
            float fx = pw / 256f, fy = ph / 256f;
            float topTrim = ph - (off.y + th); // rows trimmed above the texture rect
            int W = 170 * S, H = 140 * S;
            var a = new bool[W * H];
            float c = Mathf.Cos(Tilt * Mathf.Deg2Rad), s = Mathf.Sin(Tilt * Mathf.Deg2Rad);
            float cx = W / 2f, cy = H / 2f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    // Inverse of a clockwise tilt (y grows downwards), then back to portrait pixels.
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float ix = dx * c + dy * s + cx, iy = -dx * s + dy * c + cy;
                    float sx = (30f + ix / S) * fx - off.x, sy = (iy / S) * fy - topTrim;
                    a[y * W + x] = SampleTopLeft(src, tw, th, sx, sy) > AlphaCut;
                }

            // Cut the neck: below the jaw the rows narrow sharply.
            int bottom = H;
            bool seen = false;
            for (int y = 0; y < H; y++)
            {
                int first = -1, last = -1;
                for (int x = 0; x < W; x++) if (a[y * W + x]) { if (first < 0) first = x; last = x; }
                int rw = first < 0 ? 0 : last - first + 1;
                if (rw > 90 * S) seen = true;
                if (seen && rw < 55 * S && y > 70 * S) { bottom = y; break; }
            }
            H = bottom;

            // Everything not reachable from outside through empty pixels is the silhouette; its empty part is the holes.
            var outside = new bool[W * H];
            var stack = new Stack<int>();
            for (int x = 0; x < W; x++) { Seed(x, 0); Seed(x, H - 1); }
            for (int y = 0; y < H; y++) { Seed(0, y); Seed(W - 1, y); }
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % W, y = i / W;
                if (x > 0) Seed(x - 1, y);
                if (x < W - 1) Seed(x + 1, y);
                if (y > 0) Seed(x, y - 1);
                if (y < H - 1) Seed(x, y + 1);
            }
            void Seed(int x, int y)
            {
                int i = y * W + x;
                if (!outside[i] && !a[i]) { outside[i] = true; stack.Push(i); }
            }

            // Crop to the silhouette, at most 166 x 99 (the recipe's 665 x 398 at 4x), and keep its main piece.
            Bounds(outside, W, H, out int bx0, out int by0, out int bx1, out int by1);
            int cw = Math.Min(bx1 - bx0 + 1, 665 * S / 4), ch = Math.Min(by1 - by0 + 1, 398 * S / 4);
            var sil = new bool[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++) sil[y * cw + x] = !outside[(y + by0) * W + x + bx0];
            var main = MainPiece(sil, cw, ch, cw / 2, 10 * S);
            Bounds(Invert(main), cw, ch, out int mx0, out int my0, out int mx1, out int my1);

            w = mx1 - mx0 + 1; h = my1 - my0 + 1;
            bone = new float[w * h]; holes = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int ci = (y + my0) * cw + x + mx0;
                    if (!main[ci]) continue;
                    int ai = (y + my0 + by0) * W + x + mx0 + bx0;
                    if (a[ai]) bone[y * w + x] = 1f; else holes[y * w + x] = 1f;
                }
        }

        /// <summary>The sprite's alpha, row 0 = bottom (Unity's order). The texture is not CPU-readable, so it goes
        /// through a render texture.</summary>
        private static float[] ReadAlpha(Sprite sprite, out int w, out int h)
        {
            var tex = sprite.texture;
            Rect r = sprite.textureRect;
            w = Mathf.RoundToInt(r.width); h = Mathf.RoundToInt(r.height);
            if (w <= 0 || h <= 0) throw new InvalidOperationException("empty portrait");
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Texture2D? read = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0, false);
                read.Apply(false);
                var px = read.GetPixels32();
                var alpha = new float[px.Length];
                for (int i = 0; i < px.Length; i++) alpha[i] = px[i].a / 255f;
                return alpha;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (read != null) UnityEngine.Object.Destroy(read);
            }
        }

        private static string SafeTextureRect(Sprite s)
        {
            try { var r = s.textureRect; return $"{r.x},{r.y} {r.width}x{r.height}"; } catch { return "n/a"; }
        }

        /// <summary>Bilinear alpha at (x, y) measured from the top left in pixels; outside is transparent.</summary>
        private static float SampleTopLeft(float[] bottomUp, int w, int h, float x, float y)
        {
            float u = x - 0.5f, v = (h - y) - 0.5f; // to bottom-up pixel centres
            int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
            float tx = u - x0, ty = v - y0;
            float At(int px, int py) => px < 0 || py < 0 || px >= w || py >= h ? 0f : bottomUp[py * w + px];
            return Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), tx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), tx), ty);
        }

        private static bool[] Invert(bool[] m) { var r = new bool[m.Length]; for (int i = 0; i < m.Length; i++) r[i] = !m[i]; return r; }

        /// <summary>The bounding box of the cells that are false.</summary>
        private static void Bounds(bool[] empty, int w, int h, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = w; y0 = h; x1 = -1; y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (!empty[y * w + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < 0) throw new InvalidOperationException("no skull in the portrait");
        }

        /// <summary>The 4-connected piece of <paramref name="m"/> containing (sx, sy), or the largest piece when that cell
        /// is empty.</summary>
        private static bool[] MainPiece(bool[] m, int w, int h, int sx, int sy)
        {
            var label = new int[m.Length];
            int best = 0, bestSize = 0, startLabel = 0, next = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < m.Length; i++)
            {
                if (!m[i] || label[i] != 0) continue;
                int id = ++next, size = 0;
                label[i] = id; stack.Push(i);
                while (stack.Count > 0)
                {
                    int j = stack.Pop(), x = j % w, y = j / w;
                    size++;
                    if (x > 0) Visit(j - 1);
                    if (x < w - 1) Visit(j + 1);
                    if (y > 0) Visit(j - w);
                    if (y < h - 1) Visit(j + w);
                    void Visit(int k) { if (m[k] && label[k] == 0) { label[k] = id; stack.Push(k); } }
                }
                if (size > bestSize) { bestSize = size; best = id; }
            }
            if (sx >= 0 && sy >= 0 && sx < w && sy < h) startLabel = label[sy * w + sx];
            int keep = startLabel != 0 ? startLabel : best;
            var r = new bool[m.Length];
            for (int i = 0; i < m.Length; i++) r[i] = label[i] == keep;
            return r;
        }

        // ----------------------------------------------------------------- the icons

        // Layout in the design's 720 x 720 canvas (one in front, the others behind on each side), cropped to
        // (33, 281)-(686, 690) and scaled to OutWidth.
        private const float CropX = 33f, CropY = 281f, CropW = 653f;
        private const float FrontWidth = 430f, HeadGap = 14f;

        private struct Head { public Color32 Color; public float Width, CenterX, Bottom; }

        private static Head[] Layout(int n)
        {
            var front = new Head { Color = Front, Width = FrontWidth, CenterX = 360f, Bottom = 690f };
            if (n == 2)
                return new[] { new Head { Color = Back1, Width = (int)(FrontWidth * .72f), CenterX = 215f, Bottom = 515f },
                               new Head { Color = Front, Width = FrontWidth, CenterX = 415f, Bottom = 690f } };
            if (n == 3)
                return new[] { new Head { Color = Back2, Width = (int)(FrontWidth * .66f), CenterX = 175f, Bottom = 530f },
                               new Head { Color = Back2, Width = (int)(FrontWidth * .66f), CenterX = 545f, Bottom = 530f },
                               front };
            return new[] { front };
        }

        /// <summary>One icon: <paramref name="n"/> skulls (0 = the arrow alone) over the arrow, as a sprite.</summary>
        private static Sprite Compose(int n, float[]? bone, float[]? holes, int bw, int bh)
        {
            // Straight-alpha RGBA, row 0 = top.
            var rgb = new Vector3[OutWidth * OutHeight];
            var alpha = new float[OutWidth * OutHeight];
            float k = OutWidth / CropW; // canvas units -> output pixels

            if (n > 0 && bone != null && holes != null)
            {
                foreach (var head in Layout(n))
                {
                    float hw = head.Width, hh = bh * hw / bw;
                    float left = (head.CenterX - hw / 2f - CropX) * k, top = (head.Bottom - hh - CropY) * k;
                    float pw = hw * k, ph = hh * k;
                    var cover = new float[OutWidth * SkullAreaHeight];
                    var boneShare = new float[OutWidth * SkullAreaHeight];
                    int x0 = Math.Max(0, Mathf.FloorToInt(left)), x1 = Math.Min(OutWidth - 1, Mathf.CeilToInt(left + pw));
                    int y0 = Math.Max(0, Mathf.FloorToInt(top)), y1 = Math.Min(SkullAreaHeight - 1, Mathf.CeilToInt(top + ph));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float b = 0f, o = 0f;
                            for (int sy = 0; sy < 2; sy++)
                                for (int sx = 0; sx < 2; sx++)
                                {
                                    float u = (x + 0.25f + 0.5f * sx - left) / pw * bw, v = (y + 0.25f + 0.5f * sy - top) / ph * bh;
                                    b += Sample(bone, bw, bh, u, v);
                                    o += Sample(holes, bw, bh, u, v);
                                }
                            b *= 0.25f; o *= 0.25f;
                            float cv = Mathf.Clamp01(b + o);
                            int i = y * OutWidth + x;
                            cover[i] = cv;
                            boneShare[i] = cv > 0f ? b / (b + o) : 0f;
                        }

                    // A clean gap around this head in whatever is already drawn behind it.
                    var ring = Dilate(cover, OutWidth, SkullAreaHeight, Mathf.RoundToInt(HeadGap * k));
                    for (int i = 0; i < ring.Length; i++) alpha[i] = Mathf.Max(0f, alpha[i] - ring[i]);

                    var col = new Vector3(head.Color.r, head.Color.g, head.Color.b);
                    var dark = new Vector3(Socket.r, Socket.g, Socket.b);
                    for (int i = 0; i < cover.Length; i++)
                    {
                        float sa = cover[i];
                        if (sa <= 0f) continue;
                        Vector3 sc = Vector3.Lerp(dark, col, boneShare[i]);
                        float da = alpha[i], oa = sa + da * (1f - sa);
                        rgb[i] = oa > 0f ? (sc * sa + rgb[i] * da * (1f - sa)) / oa : Vector3.zero;
                        alpha[i] = oa;
                    }
                }
            }

            // The arrow: a downward triangle under the skulls, 4x4 supersampled.
            int ax = (OutWidth - ArrowWidth) / 2, ay = SkullAreaHeight + ArrowGap;
            var front = new Vector3(Front.r, Front.g, Front.b);
            for (int y = 0; y < ArrowHeight; y++)
                for (int x = 0; x < ArrowWidth; x++)
                {
                    int inside = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float px = x + (sx + 0.5f) / 4f, py = y + (sy + 0.5f) / 4f;
                            float half = ArrowWidth / 2f * (1f - py / ArrowHeight);
                            if (Mathf.Abs(px - ArrowWidth / 2f) <= half) inside++;
                        }
                    if (inside == 0) continue;
                    int i = (ay + y) * OutWidth + ax + x;
                    rgb[i] = front; alpha[i] = inside / 16f;
                }

            BleedColour(rgb, alpha, OutWidth, OutHeight, passes: 12);

            var px32 = new Color32[OutWidth * OutHeight];
            for (int y = 0; y < OutHeight; y++)
                for (int x = 0; x < OutWidth; x++)
                {
                    int i = y * OutWidth + x, o = (OutHeight - 1 - y) * OutWidth + x; // to Unity's bottom-up rows
                    var c = rgb[i];
                    px32[o] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(c.x), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.y), 0, 255),
                                          (byte)Mathf.Clamp(Mathf.RoundToInt(c.z), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(alpha[i] * 255f), 0, 255));
                }
            var tex = new Texture2D(OutWidth, OutHeight, TextureFormat.RGBA32, mipChain: true)
            {
                name = $"SULFURTogether.TeamMark{n}", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(px32);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            // Pivot at the arrow's tip (bottom centre, past the padding), so the icon sits on the point it marks.
            var sprite = Sprite.Create(tex, new Rect(0, 0, OutWidth, OutHeight), new Vector2(0.5f, (float)BottomPad / OutHeight), 200f, 0, SpriteMeshType.FullRect);
            sprite.name = tex.name;
            return sprite;
        }

        /// <summary>Give fully transparent pixels the average colour of their covered neighbours, ring by ring, so
        /// bilinear and mipmap filtering blend an edge toward its own colour instead of toward black.</summary>
        private static void BleedColour(Vector3[] rgb, float[] alpha, int w, int h, int passes)
        {
            var filled = new bool[alpha.Length];
            for (int i = 0; i < alpha.Length; i++) filled[i] = alpha[i] > 0f;
            var next = new List<int>();
            for (int pass = 0; pass < passes; pass++)
            {
                next.Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (filled[i]) continue;
                        Vector3 sum = Vector3.zero;
                        int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int j = ny * w + nx;
                                if (!filled[j]) continue;
                                sum += rgb[j]; n++;
                            }
                        if (n == 0) continue;
                        rgb[i] = sum / n;
                        next.Add(i);
                    }
                if (next.Count == 0) break;
                foreach (int i in next) filled[i] = true;
            }
        }

        /// <summary>Bilinear sample of a top-down mask at (u, v) in its pixels; outside is empty.</summary>
        private static float Sample(float[] m, int w, int h, float u, float v)
        {
            float fu = u - 0.5f, fv = v - 0.5f;
            int x0 = Mathf.FloorToInt(fu), y0 = Mathf.FloorToInt(fv);
            float tx = fu - x0, ty = fv - y0;
            float At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0f : m[y * w + x];
            return Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), tx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), tx), ty);
        }

        /// <summary>A square max filter of radius <paramref name="r"/> (the recipe's MaxFilter).</summary>
        private static float[] Dilate(float[] m, int w, int h, int r)
        {
            if (r <= 0) return (float[])m.Clone();
            var tmp = new float[m.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float best = 0f;
                    for (int d = Math.Max(0, x - r); d <= Math.Min(w - 1, x + r); d++) best = Math.Max(best, m[y * w + d]);
                    tmp[y * w + x] = best;
                }
            var outp = new float[m.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float best = 0f;
                    for (int d = Math.Max(0, y - r); d <= Math.Min(h - 1, y + r); d++) best = Math.Max(best, tmp[d * w + x]);
                    outp[y * w + x] = best;
                }
            return outp;
        }
    }
}
