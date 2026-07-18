using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.UI.Shared;

namespace SULFURTogether.UI.EndlessCardVote
{
    /// <summary>
    /// EM-6b-3a shared-mode card-vote feedback. Two parts, both driven from the one authoritative
    /// <see cref="EndlessCardVoteManager.Current"/> snapshot (never any local vote decision):
    /// <list type="bullet">
    /// <item><b>On-card stamps</b> — each voter's name is "stamped" onto the 3D card they voted for, rubber-stamp style
    /// (bold, tilted at a per-voter random angle, jittered so two stamps on one card don't perfectly overlap). The local
    /// player's stamp is a warm gold ink; teammates' stamps are red ink. Stamps are cloned from the card's own subtitle
    /// text object so they inherit the exact font, face orientation, and follow-the-camera motion for free, and are
    /// destroyed with the card when <c>SpinAndDismissCard</c> tears the panel down.</item>
    /// <item><b>Status hint</b> — a small bottom-centre line: a "aim &amp; fire to vote" prompt before anyone has voted
    /// (no countdown yet — deliberation is unbounded until the first cast), then "waiting for teammates n/m — Xs" while
    /// the timeout runs, then the resolved card number.</item>
    /// </list>
    /// This is passive display only; it never blocks input and self-gates to a live shared-mode Endless card vote.
    /// </summary>
    internal static class EndlessCardVoteOverlay
    {
        private static readonly Color SelfInk   = new Color(1f, 0.72f, 0.15f, 0.96f);   // gold   — the local player's pick
        private static readonly Color MateInk   = new Color(0.88f, 0.20f, 0.16f, 0.96f); // red    — teammates' picks
        private static readonly Color BanishInk = new Color(0.74f, 0.28f, 0.95f, 0.97f); // violet — a banish vote (either player)

        private sealed class Stamp { public GameObject? Go; public int CardIndex; public int Stack; }
        private static readonly Dictionary<string, Stamp> _stamps = new Dictionary<string, Stamp>();

        private static GameObject? _hudRoot;
        private static CanvasGroup? _hudCg;
        private static TextMeshProUGUI? _hudText;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }

        public static void Tick()
        {
            try { TickInner(); }
            catch (Exception ex) { WarnOnce($"tick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void Shutdown()
        {
            ClearStamps();
            if (_hudRoot != null) _hudRoot.SetActive(false);
        }

        private static void TickInner()
        {
            var snap = EndlessCardVoteManager.Current;
            bool active = Enabled && snap != null && snap.CardEventId > 0
                          && NetGameplaySyncBridge.BossMode != NetMode.Off && !EndlessSyncManager.IsIndependentMode;
            if (!active) { ClearStamps(); HideHud(); return; }

            ShowHud(StatusText(snap!));

            // Tie/no-vote raffle: the native card highlight sweep is driven by the FloatingCardManager.Update postfix
            // (EndlessSyncPatches). Keep reconciling the stamps so every voter's stamp is shown during the sweep — the
            // last vote resolves the tie instantly, so its stamp is created here rather than before the raffle began.
            if (EndlessCardVoteManager.RaffleActive) { ReconcileStamps(snap!); return; }

            if (snap!.Phase == 1) { _stamps.Clear(); return; } // resolved → cards carry their stamps out; drop refs
            ReleaseStampsOnBanishedCards(snap); // let a winning ✕ stamp ride the banished card out instead of being destroyed
            ReconcileStamps(snap);
        }

        // A card that just got banished is animating out (StartDismissal). Its ✕ stamp is a child of that card, so releasing
        // it (drop our reference without destroying) lets it ride the card out and die with it — otherwise the vote-clear
        // that accompanies a banish would make ReconcileStamps destroy it immediately, before the player sees it.
        private static void ReleaseStampsOnBanishedCards(NetEndlessCardVoteState snap)
        {
            if (snap.BanishedIndices == null || snap.BanishedIndices.Length == 0 || _stamps.Count == 0) return;
            List<string>? release = null;
            foreach (var kv in _stamps)
                if (Array.IndexOf(snap.BanishedIndices, kv.Value.CardIndex) >= 0)
                    (release ??= new List<string>()).Add(kv.Key);
            if (release != null) foreach (var k in release) _stamps.Remove(k);
        }

        // ---------------------------------------------------------------- on-card stamps

        private static void ReconcileStamps(NetEndlessCardVoteState snap)
        {
            object? fcm = EndlessCardManager.ResolveLocalCardManager();
            Array? cards = fcm != null ? EndlessCardManager.GetSpawnedCards(fcm) : null;
            if (fcm == null || cards == null) { ClearStamps(); return; }

            string me = NetGameplaySyncBridge.LocalPeerId;

            // Desired: each participant contributes a PICK stamp (on its voted card) and/or a BANISH stamp (on the card it
            // is voting to banish, EM-6b-3c). Keyed by "peer|kind" so one player can show both. Stacked per card so stamps
            // on the same card don't perfectly overlap.
            var desired = new Dictionary<string, (int card, int stack, string name, bool self, bool banish)>();
            var perCard = new Dictionary<int, int>();
            if (snap.Participants != null)
            {
                foreach (var p in snap.Participants)
                {
                    bool self = string.Equals(p.PeerId, me, StringComparison.Ordinal);
                    string name = string.IsNullOrEmpty(p.Name) ? p.PeerId : p.Name;
                    AddDesired(desired, perCard, cards, p.PeerId + "|p", p.VotedIndex,  name, self, false);
                    AddDesired(desired, perCard, cards, p.PeerId + "|b", p.BanishIndex, name, self, true);
                }
            }

            // Remove stamps for peers who cleared/never voted (or whose card is gone).
            var stale = new List<string>();
            foreach (var kv in _stamps) if (!desired.ContainsKey(kv.Key)) stale.Add(kv.Key);
            foreach (var k in stale) { DestroyStamp(_stamps[k]); _stamps.Remove(k); }

            // Create/refresh.
            foreach (var kv in desired)
            {
                var d = kv.Value;
                if (_stamps.TryGetValue(kv.Key, out var ex) && ex.Go != null && ex.CardIndex == d.card && ex.Stack == d.stack)
                    continue;
                if (_stamps.TryGetValue(kv.Key, out var old)) { DestroyStamp(old); _stamps.Remove(kv.Key); }
                var go = CreateStamp(cards.GetValue(d.card)!, kv.Key, d.card, d.name, d.self, d.banish);
                if (go != null) _stamps[kv.Key] = new Stamp { Go = go, CardIndex = d.card, Stack = d.stack };
            }
        }

        private static void AddDesired(
            Dictionary<string, (int card, int stack, string name, bool self, bool banish)> desired,
            Dictionary<int, int> perCard, Array cards, string key, int cardIndex, string name, bool self, bool banish)
        {
            if (cardIndex < 0 || cardIndex >= cards.Length || cards.GetValue(cardIndex) == null) return;
            int stack = perCard.TryGetValue(cardIndex, out var c) ? c : 0;
            perCard[cardIndex] = stack + 1;
            desired[key] = (cardIndex, stack, name, self, banish);
        }

        private static GameObject? CreateStamp(object card, string peerId, int cardIndex, string name, bool self, bool banish)
        {
            try
            {
                if (EndlessCardManager.GetCardSubtitle(card) is not TMP_Text sub || sub == null) return null;
                // worldPositionStays:false → the clone keeps the subtitle's LOCAL transform (position/rotation/scale), the
                // correct way to clone a UI child so it sits exactly where the subtitle is (the worldPositionStays:true
                // default was blowing the local scale up — the giant on-screen name).
                var clone = UnityEngine.Object.Instantiate(sub.gameObject, sub.transform.parent, false);
                clone.name = "STVoteStamp";
                for (int i = clone.transform.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(clone.transform.GetChild(i).gameObject); // subtitle carries no children, but be safe
                if (clone.GetComponent<TMP_Text>() is not TMP_Text tmp) { UnityEngine.Object.Destroy(clone); return null; }

                // A banish stamp is prefixed with ✕ and inked violet (distinct from a gold/red pick stamp) so it reads as
                // "wants this card gone" regardless of which player cast it; the name still identifies the voter.
                tmp.text = (banish ? "✕ " : "") + (name ?? "").ToUpperInvariant();
                tmp.color = banish ? BanishInk : (self ? SelfInk : MateInk);
                tmp.fontStyle = FontStyles.Bold;
                tmp.enableAutoSizing = false;
                tmp.fontSize = sub.enableAutoSizing ? Mathf.Max(sub.fontSizeMin, 8f) : sub.fontSize; // reference size; rescaled below
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.raycastTarget = false;

                // Robustly match the stamp's rendered HEIGHT to the subtitle's actual rendered height — independent of the
                // card's font size / auto-size / unit system (px vs world). bounds are in local space (pre-localScale), so
                // scaling localScale by subH/myH makes the two world-heights equal.
                sub.ForceMeshUpdate();
                tmp.ForceMeshUpdate();
                float subH = sub.bounds.size.y;
                float myH  = tmp.bounds.size.y;
                float fit  = (myH > 1e-4f && subH > 1e-4f) ? Mathf.Clamp(subH / myH, 0.02f, 4f) : 1f;

                var rnd  = new System.Random(((peerId ?? "").GetHashCode() * 397) ^ cardIndex);
                float tilt = (float)(rnd.NextDouble() * 2.0 - 1.0) * 14f; // ±14°
                float jx   = (float)(rnd.NextDouble() * 2.0 - 1.0);
                float jy   = (float)(rnd.NextDouble() * 2.0 - 1.0);

                var t = clone.transform;
                t.localRotation = sub.transform.localRotation * Quaternion.Euler(0f, 0f, tilt);
                t.localScale    = sub.transform.localScale * (fit * 1.15f); // ~subtitle height, ×1.15 stamp emphasis
                if (t is RectTransform rt && sub.transform is RectTransform srt)
                {
                    Vector2 sz = srt.rect.size;
                    rt.anchoredPosition3D = srt.anchoredPosition3D + new Vector3(jx * sz.x * 0.10f, jy * sz.y * 0.10f, 0f);
                }
                var cam = ResolvePlayerCamera();
                if (cam != null) t.position += (cam.transform.position - t.position).normalized * 0.02f;

                // Black outline for legibility over the card art (done AFTER the bounds measurement so it doesn't skew the
                // size match). Use a per-stamp fontMaterial instance (never the shared card font), enable the SDF outline
                // keyword explicitly, and UpdateMeshPadding so the outline isn't clipped at the glyph edges (the reason a
                // bare outlineWidth shows nothing).
                try
                {
                    var mat = tmp.fontMaterial;
                    mat.EnableKeyword("OUTLINE_ON");
                    mat.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.3f);
                    tmp.UpdateMeshPadding();
                    tmp.ForceMeshUpdate();
                }
                catch (Exception ex) { WarnOnce($"stamp outline failed: {ex.GetType().Name}: {ex.Message}"); }

                return clone;
            }
            catch (Exception ex) { WarnOnce($"create stamp failed: {ex.GetType().Name}: {ex.Message}"); return null; }
        }

        private static Camera? ResolvePlayerCamera()
        {
            try { return Camera.main; } catch { return null; }
        }

        private static void DestroyStamp(Stamp? s)
        {
            try { if (s?.Go != null) UnityEngine.Object.Destroy(s.Go); } catch { }
        }

        private static void ClearStamps()
        {
            if (_stamps.Count == 0) return;
            foreach (var kv in _stamps) DestroyStamp(kv.Value);
            _stamps.Clear();
        }

        // ---------------------------------------------------------------- status hint

        private static string StatusText(NetEndlessCardVoteState snap)
        {
            int total = snap.Participants?.Length ?? 0;
            int voted = 0;
            if (snap.Participants != null) foreach (var p in snap.Participants) if (p.VotedIndex >= 0 || p.BanishIndex >= 0) voted++;

            if (EndlessCardVoteManager.RaffleActive) // tie → don't reveal the winner until the sweep lands
                return CoopLoc.Get("endless.cardvote.rolling", "Tie — drawing a card…");
            if (snap.Phase == 1)
            {
                switch (EndlessCardManager.DescribeResolvedCard(snap.ResolvedIndex)) // EM-6b-3b: name the Skip/Reroll outcome
                {
                    case 2: return CoopLoc.Get("endless.cardvote.resolved_reroll", "Rerolling cards…");
                    case 1: return CoopLoc.Get("endless.cardvote.resolved_skip", "Skipped");
                }
                return CoopLoc.Format("endless.cardvote.resolved", "Card {index} chosen",
                    ("index", (snap.ResolvedIndex + 1).ToString()));
            }
            if (!snap.TimeoutActive)
                return CoopLoc.Get("endless.cardvote.prompt", "Aim at a card and fire to vote");
            return CoopLoc.Format("endless.cardvote.waiting", "Waiting for teammates… {voted}/{total} — {secs}s",
                ("voted", voted.ToString()), ("total", total.ToString()),
                ("secs", Mathf.CeilToInt(snap.SecondsRemaining).ToString()));
        }

        private static void ShowHud(string text)
        {
            EnsureHud();
            if (_hudRoot == null || _hudText == null || _hudCg == null) return;
            if (!_hudRoot.activeSelf) _hudRoot.SetActive(true);
            _hudCg.alpha = 1f;
            _hudText.text = text;
        }

        private static void HideHud()
        {
            if (_hudRoot != null && _hudRoot.activeSelf) _hudRoot.SetActive(false);
        }

        private static void EnsureHud()
        {
            if (_hudRoot != null) return;
            var root = new GameObject("STEndlessCardVoteHud", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _hudCg = root.GetComponent<CanvasGroup>();
            _hudCg.blocksRaycasts = false;
            _hudCg.interactable = false;

            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(root.transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0f); rect.anchorMax = new Vector2(0.5f, 0f); rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 230f); // above the hotbar; tune against the real HUD
            rect.sizeDelta = new Vector2(1000f, 60f);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            var font = NativeFontSampler.ResolveNativeFont();
            if (font != null) tmp.font = font;
            tmp.fontSize = 30f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.99f, 0.97f, 0.94f, 1f);
            tmp.fontStyle = FontStyles.Bold;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            try { tmp.outlineWidth = 0.2f; tmp.outlineColor = Color.black; } catch { } // readability over the 3D scene

            _hudText = tmp;
            _hudRoot = root;
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.SetActive(false);
        }

        private static string _lastWarned = "";
        private static void WarnOnce(string message)
        {
            if (message == _lastWarned) return;
            _lastWarned = message;
            Plugin.Log?.Warn($"[EndlessCardVote] {message}");
        }
    }
}
