using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-6b: Shared-mode card selection is a group choice over the <b>host-rolled</b> card set. The host owns the
    /// card RNG (<c>EndlessModeManager.gameplayRandom</c>); the client's Endless manager is a slave with a diverged random
    /// stream, so it can never roll the same cards. The host therefore captures the N cards the moment its panel finishes
    /// spawning (<c>FloatingCardManager.cardsSetup</c>) and broadcasts the list; every client mirrors that authoritative
    /// set to display and vote on the same options.
    ///
    /// <para>This first slice (EM-6b-1) captures + broadcasts the manifest and logs it on the client — validating the
    /// capture point and the wire before the mirror UI + vote are built on top (EM-6b-2/3).</para>
    /// </summary>
    internal static class EndlessCardManager
    {
        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }
        private static bool SharedMode => EndlessSyncManager.IsIndependentMode == false;

        // ---- cached reflection ----
        private static bool _resolved;
        private static FieldInfo? _emCardManager;   // EndlessModeManager.cardManager : FloatingCardManager
        private static FieldInfo? _fcmCardsSetup;    // FloatingCardManager.cardsSetup : bool
        private static FieldInfo? _fcmSpawnedCards;  // FloatingCardManager.spawnedCards : FloatingCard[]
        private static FieldInfo? _fcmCardRewards;   // FloatingCardManager.cardRewards : CardReward[]
        private static FieldInfo? _fcIsStatic;       // FloatingCard.isStaticCard : bool
        private static FieldInfo? _fcSubtitle;       // FloatingCard.subtitleText : TMP_Text
        private static FieldInfo? _fcDescription;    // FloatingCard.descriptionText : TMP_Text
        private static FieldInfo? _crCardKey;        // CardReward.cardKey : string
        private static PropertyInfo? _tmpText;       // TMP_Text.text : string

        // ---- host capture edge ----
        private static bool _hostCaptured;   // captured the current panel's cards already
        private static int  _cardEventId;    // monotonic per host run

        // ---- client dedup ----
        private static int _clientLastEvent = -1;

        public static void Reset()
        {
            _hostCaptured = false;
            _cardEventId = 0;
            _clientLastEvent = -1;
        }

        /// <summary>HOST (Shared mode, each service tick): when the card panel finishes spawning, capture the rolled cards
        /// once and broadcast them. The <c>cardsSetup</c> flag is true exactly while the panel is up, so its rising edge is
        /// the canonical "cards ready" signal and its falling edge ends the event.</summary>
        public static void HostTick()
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                EnsureResolved();
                object? fcm = ResolveCardManager();
                if (fcm == null) { _hostCaptured = false; return; }

                bool ready = _fcmCardsSetup?.GetValue(fcm) is bool b && b;
                if (!ready) { _hostCaptured = false; return; } // panel down → arm for the next event
                if (_hostCaptured) return;                     // already captured this panel

                var msg = BuildManifest(fcm);
                if (msg == null) return; // arrays not populated yet this frame — retry next tick (still not captured)
                _hostCaptured = true;

                NetGameplaySyncBridge.BroadcastHostEndlessCardManifest(msg);
                ApplyManifest(msg); // the host keeps the same authoritative record (mirror UI + vote build on it in EM-6b-2/3)
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b host card manifest event={msg.CardEventId} count={msg.Cards.Length} keys=[{KeysOf(msg)}]");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] card HostTick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static NetEndlessCardManifest? BuildManifest(object fcm)
        {
            if (_fcmSpawnedCards?.GetValue(fcm) is not Array cards || cards.Length == 0) return null;
            var rewards = _fcmCardRewards?.GetValue(fcm) as Array;

            int n = cards.Length;
            var entries = new NetEndlessCardManifest.CardEntry[n];
            for (int i = 0; i < n; i++)
            {
                object? card = cards.GetValue(i);
                object? reward = rewards != null && i < rewards.Length ? rewards.GetValue(i) : null;
                bool isStatic = card != null && _fcIsStatic?.GetValue(card) is bool s && s;
                entries[i] = new NetEndlessCardManifest.CardEntry
                {
                    Key      = reward != null ? (_crCardKey?.GetValue(reward) as string ?? "") : "",
                    IsStatic = isStatic,
                    Title    = ReadTmp(card, _fcSubtitle),
                    Desc     = ReadTmp(card, _fcDescription),
                };
            }

            if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed))
            { chap = ""; lvl = -1; hasSeed = false; seed = 0; }

            return new NetEndlessCardManifest
            {
                ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                CardEventId = ++_cardEventId, Cards = entries,
            };
        }

        /// <summary>BOTH ENDS: record the authoritative card set for the current event. EM-6b-1 only logs it on the client;
        /// the mirror panel + vote are built on this record in EM-6b-2/3.</summary>
        public static void ApplyManifest(NetEndlessCardManifest msg)
        {
            try
            {
                if (!Enabled || msg == null) return;
                if (NetGameplaySyncBridge.BossMode == NetMode.Client)
                {
                    if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                        && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                        return; // different level
                    if (msg.CardEventId == _clientLastEvent) return; // dedup
                    _clientLastEvent = msg.CardEventId;
                    if (LogOn) Plugin.Log.Info($"[Endless] EM-6b client card manifest event={msg.CardEventId} count={msg.Cards.Length} keys=[{KeysOf(msg)}]");
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ApplyManifest failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string KeysOf(NetEndlessCardManifest msg)
        {
            try
            {
                var parts = new string[msg.Cards.Length];
                for (int i = 0; i < msg.Cards.Length; i++)
                {
                    var c = msg.Cards[i];
                    parts[i] = c.IsStatic ? (string.IsNullOrEmpty(c.Key) ? "<static>" : c.Key) : c.Key;
                }
                return string.Join(",", parts);
            }
            catch { return "?"; }
        }

        private static string ReadTmp(object? card, FieldInfo? tmpField)
        {
            try
            {
                if (card == null || tmpField == null || _tmpText == null) return "";
                object? tmp = tmpField.GetValue(card);
                if (tmp is UnityEngine.Object uo && uo == null) return "";
                return _tmpText.GetValue(tmp) as string ?? "";
            }
            catch { return ""; }
        }

        private static object? ResolveCardManager()
        {
            try
            {
                object? mgr = EndlessSyncManager.ResolveEndlessInstance();
                if (mgr == null || _emCardManager == null) return null;
                object? fcm = _emCardManager.GetValue(mgr);
                if (fcm is UnityEngine.Object uo && uo == null) return null;
                return fcm;
            }
            catch { return null; }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var emType = AccessTools.TypeByName("EndlessModeManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.EndlessModeManager");
                var fcmType = AccessTools.TypeByName("FloatingCardManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.FloatingCardManager");
                var fcType = AccessTools.TypeByName("FloatingCard") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.FloatingCard");
                var crType = AccessTools.TypeByName("CardReward") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.CardReward");
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                _emCardManager   = emType?.GetField("cardManager", bf);
                _fcmCardsSetup   = fcmType?.GetField("cardsSetup", bf);
                _fcmSpawnedCards = fcmType?.GetField("spawnedCards", bf);
                _fcmCardRewards  = fcmType?.GetField("cardRewards", bf);
                _fcIsStatic      = fcType?.GetField("isStaticCard", bf);
                _fcSubtitle      = fcType?.GetField("subtitleText", bf);
                _fcDescription   = fcType?.GetField("descriptionText", bf);
                _crCardKey       = crType?.GetField("cardKey", bf);

                var tmpType = _fcSubtitle?.FieldType;
                _tmpText = tmpType?.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);

                Plugin.Log.Info($"[Endless] EM-6b resolved cardMgr={_emCardManager != null} cardsSetup={_fcmCardsSetup != null} " +
                                $"spawned={_fcmSpawnedCards != null} rewards={_fcmCardRewards != null} isStatic={_fcIsStatic != null} " +
                                $"subtitle={_fcSubtitle != null} desc={_fcDescription != null} key={_crCardKey != null} tmpText={_tmpText != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-6b EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
