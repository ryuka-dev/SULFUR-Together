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
        private static FieldInfo? _fcmCardsVisible;   // FloatingCardManager.cardsVisible : bool
        private static FieldInfo? _fcmSpawnedCards;  // FloatingCardManager.spawnedCards : FloatingCard[]
        private static FieldInfo? _fcmCardRewards;   // FloatingCardManager.cardRewards : CardReward[]
        private static FieldInfo? _fcIsStatic;       // FloatingCard.isStaticCard : bool
        private static FieldInfo? _fcSubtitle;       // FloatingCard.subtitleText : TMP_Text
        private static FieldInfo? _fcDescription;    // FloatingCard.descriptionText : TMP_Text
        private static FieldInfo? _crCardKey;        // CardReward.cardKey : string
        private static PropertyInfo? _tmpText;       // TMP_Text.text : string
        private static MethodInfo? _fcmSpawnCards;   // FloatingCardManager.SpawnCards()
        private static MethodInfo? _fcmExitCards;    // FloatingCardManager.ExitCardSelection() — unsubscribes input + resets

        // gameplayRandom (Unity.Mathematics.Random struct) + its inner state
        private static FieldInfo? _emGameplayRandom; // EndlessModeManager.gameplayRandom : Unity.Mathematics.Random
        private static FieldInfo? _randState;        // Unity.Mathematics.Random.state : uint
        // card-manager selection state
        private static FieldInfo? _selOneTime, _banishedKeys, _forcedPreselect, _removedItems, _banishesUsed;
        private static FieldInfo? _biCardKey, _biItemName; // BanishedItemEntry.{cardKey,itemName}
        private static Type? _biType;                       // BanishedItemEntry
        // EndlessModeManager reroll/banish remaining (auto-property backing fields)
        private static FieldInfo? _emRerollsBF, _emBanishesBF;

        // ---- host capture edge ----
        private static bool _hostCaptured;   // captured the current panel's cards already
        private static int  _cardEventId;    // monotonic per host run

        // ---- client dedup / driven roll ----
        private static int    _clientLastEvent = -1;
        private static int    _clientRollEvent = -1;   // event id of the roll currently being replayed on the client
        private static int    _clientChoiceDrawOverride; // host ChoiceDrawAmount forced onto the client while replaying
        private static int    _clientExpectedEvent = -1; // manifest event whose keys the client should reproduce
        private static string _clientExpectedKeys = "";  // authoritative keys from that manifest
        private static int    _clientVerifiedEvent = -1;  // last event the client logged a roll-verify for

        /// <summary>CLIENT: true while a host-driven card roll is being replayed — used to force ChoiceDrawAmount parity and
        /// to block the client's own card pick (the pick becomes a vote in EM-6b-3).</summary>
        public static bool ClientRollActive { get; private set; }
        public static int  ClientChoiceDrawOverride => _clientChoiceDrawOverride;

        public static void Reset()
        {
            _hostCaptured = false;
            _cardEventId = 0;
            _clientLastEvent = -1;
            _clientRollEvent = -1;
            _clientExpectedEvent = -1;
            _clientExpectedKeys = "";
            _clientVerifiedEvent = -1;
            ClientRollActive = false;
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

        // ================================================================== EM-6b-2 roll-state replay (plan A)

        /// <summary>HOST (Shared mode): capture the card RNG + selection state <b>before</b> the vanilla roll consumes it, and
        /// broadcast it so the client can reproduce byte-identical cards by running the same roll. Called from a
        /// <c>FloatingCardManager.SpawnCards</c> prefix — at that point <c>gameplayRandom</c> is still at its pre-roll state
        /// (the card-select window suppresses enemy spawns, the only other Endless consumer).</summary>
        public static void HostCaptureRoll(object fcm)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                EnsureResolved();
                object? mgr = EndlessSyncManager.ResolveEndlessInstance();
                if (mgr == null || fcm == null) return;

                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed))
                { chap = ""; lvl = -1; hasSeed = false; seed = 0; }

                var msg = new NetEndlessCardRoll
                {
                    ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                    CardEventId = _cardEventId + 1, // matches the manifest minted for this same panel (HostTick ++ later)
                    RandomState = ReadRandomState(mgr),
                    ChoiceDraw = ReadIntMember(mgr, "ChoiceDrawAmount"),
                    RerollsRemaining = _emRerollsBF?.GetValue(mgr) is int rr ? rr : 0,
                    BanishesRemaining = _emBanishesBF?.GetValue(mgr) is int br ? br : 0,
                    BanishesUsedThisSession = _banishesUsed?.GetValue(fcm) is int bu ? bu : 0,
                    SelectedOneTimeUsed = ReadStringList(fcm, _selOneTime),
                    BanishedCardKeys = ReadStringList(fcm, _banishedKeys),
                    ForcedPreselectKeys = ReadStringList(fcm, _forcedPreselect),
                    RemovedCardItems = ReadRemovedItems(fcm),
                };
                NetGameplaySyncBridge.BroadcastHostEndlessCardRoll(msg);
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b host card roll event={msg.CardEventId} state={msg.RandomState} draw={msg.ChoiceDraw} " +
                                           $"used={msg.SelectedOneTimeUsed.Length} banished={msg.BanishedCardKeys.Length}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] HostCaptureRoll failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT (Shared mode): set the local Endless manager to the host's captured state and run the vanilla roll,
        /// reproducing the same 3D cards. The pick is blocked (EM-6b-2) and becomes a group vote in EM-6b-3.</summary>
        public static void ApplyCardRoll(NetEndlessCardRoll msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client || !SharedMode) return;
                if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                    return; // different level
                if (msg.CardEventId == _clientRollEvent) return; // dedup

                EnsureResolved();
                object? mgr = EndlessSyncManager.ResolveEndlessInstance();
                object? fcm = ResolveCardManager();
                if (mgr == null || fcm == null || _fcmSpawnCards == null) return;

                // Already showing a panel? tear it down first so we never stack two.
                TeardownClientCards();

                // 1) selection state → the available pool + preselection the roll will read.
                WriteStringList(fcm, _selOneTime, msg.SelectedOneTimeUsed);
                WriteStringList(fcm, _banishedKeys, msg.BanishedCardKeys);
                WriteStringList(fcm, _forcedPreselect, msg.ForcedPreselectKeys);
                WriteRemovedItems(fcm, msg.RemovedCardItems);
                _banishesUsed?.SetValue(fcm, msg.BanishesUsedThisSession);
                _emRerollsBF?.SetValue(mgr, msg.RerollsRemaining);
                _emBanishesBF?.SetValue(mgr, msg.BanishesRemaining);

                // 2) RNG state → identical draw sequence.
                WriteRandomState(mgr, msg.RandomState);

                // 3) force the card count to the host's meta-progression value (ChoiceDrawAmount is a client-local property).
                _clientChoiceDrawOverride = Mathf.Clamp(msg.ChoiceDraw, 2, 4);
                _clientRollEvent = msg.CardEventId;
                ClientRollActive = true;

                // 4) run the unmodified vanilla roll → same cards.
                _fcmSpawnCards.Invoke(fcm, null);
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b client replaying roll event={msg.CardEventId} state={msg.RandomState} draw={_clientChoiceDrawOverride}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ApplyCardRoll failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT (each service tick): once the replayed panel has finished spawning AND the authoritative manifest
        /// for the same event has arrived, log the client's rolled keys vs the expected keys once — verifying the seed+state
        /// replay independently of message order. Cheap; only does work during an active replay.</summary>
        public static void ClientTick()
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Client || !SharedMode) return;
                if (!ClientRollActive || _clientRollEvent < 0) return;
                if (_clientVerifiedEvent == _clientRollEvent) return;          // already verified this event
                if (_clientExpectedEvent != _clientRollEvent) return;          // manifest for this event not in yet
                object? fcm = ResolveCardManager();
                if (fcm == null || !(_fcmCardsSetup?.GetValue(fcm) is bool ready && ready)) return; // panel not ready yet
                _clientVerifiedEvent = _clientRollEvent;
                string rolled = KeysOfRewards(fcm);
                bool match = string.Equals(rolled, _clientExpectedKeys, StringComparison.Ordinal);
                if (LogOn || !match)
                    Plugin.Log.Info($"[Endless] EM-6b client roll verify event={_clientRollEvent} match={match} rolled=[{rolled}] expected=[{_clientExpectedKeys}]");
            }
            catch { }
        }

        /// <summary>CLIENT: tear down a replayed card panel (destroy the floating cards + clear the manager flags) when the
        /// host leaves card selection. In EM-6b-2 there is no shared pick yet, so this is how the client's cards close.</summary>
        public static void TeardownClientCards()
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (!ClientRollActive) return; // nothing replayed
                ClientRollActive = false;
                object? fcm = ResolveCardManager();
                if (fcm == null) return;
                bool wasSetup = _fcmCardsSetup?.GetValue(fcm) is bool b && b;
                if (wasSetup) { try { _fcmExitCards?.Invoke(fcm, null); } catch { } } // unsubscribe input + reset flags (vanilla)
                if (_fcmSpawnedCards?.GetValue(fcm) is Array cards)
                {
                    for (int i = 0; i < cards.Length; i++)
                        if (cards.GetValue(i) is Component c && c != null)
                            try { UnityEngine.Object.Destroy(c.gameObject); } catch { }
                }
                _fcmCardsSetup?.SetValue(fcm, false);
                _fcmCardsVisible?.SetValue(fcm, false);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] TeardownClientCards failed: {ex.GetType().Name}: {ex.Message}"); }
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
                    _clientExpectedEvent = msg.CardEventId;         // EM-6b-2: stored so ClientTick can verify the replay
                    _clientExpectedKeys = KeysOf(msg);
                    if (LogOn) Plugin.Log.Info($"[Endless] EM-6b client card manifest event={msg.CardEventId} count={msg.Cards.Length} keys=[{_clientExpectedKeys}]");
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

        private static string KeysOfRewards(object fcm)
        {
            try
            {
                if (_fcmSpawnedCards?.GetValue(fcm) is not Array cards) return "";
                var rewards = _fcmCardRewards?.GetValue(fcm) as Array;
                var parts = new string[cards.Length];
                for (int i = 0; i < cards.Length; i++)
                {
                    object? card = cards.GetValue(i);
                    bool isStatic = card != null && _fcIsStatic?.GetValue(card) is bool s && s;
                    object? reward = rewards != null && i < rewards.Length ? rewards.GetValue(i) : null;
                    string key = reward != null ? (_crCardKey?.GetValue(reward) as string ?? "") : "";
                    parts[i] = isStatic && string.IsNullOrEmpty(key) ? "<static>" : key;
                }
                return string.Join(",", parts);
            }
            catch { return "?"; }
        }

        private static uint ReadRandomState(object mgr)
        {
            try
            {
                if (_emGameplayRandom == null || _randState == null) return 0;
                object rnd = _emGameplayRandom.GetValue(mgr); // boxed struct copy
                return _randState.GetValue(rnd) is uint u ? u : 0;
            }
            catch { return 0; }
        }

        private static void WriteRandomState(object mgr, uint state)
        {
            try
            {
                if (_emGameplayRandom == null || _randState == null) return;
                object rnd = _emGameplayRandom.GetValue(mgr); // box a copy
                _randState.SetValue(rnd, state);              // mutate the box
                _emGameplayRandom.SetValue(mgr, rnd);         // write the struct back
            }
            catch { }
        }

        private static int ReadIntMember(object obj, string name)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return Convert.ToInt32(p.GetValue(obj));
            }
            catch { }
            return 0;
        }

        private static string[] ReadStringList(object fcm, FieldInfo? field)
        {
            try
            {
                if (field?.GetValue(fcm) is System.Collections.Generic.List<string> list) return list.ToArray();
            }
            catch { }
            return Array.Empty<string>();
        }

        private static void WriteStringList(object fcm, FieldInfo? field, string[] values)
        {
            try
            {
                if (field?.GetValue(fcm) is System.Collections.Generic.List<string> list)
                {
                    list.Clear();
                    if (values != null) list.AddRange(values);
                }
            }
            catch { }
        }

        private static NetEndlessCardRoll.RemovedItem[] ReadRemovedItems(object fcm)
        {
            try
            {
                if (_removedItems?.GetValue(fcm) is System.Collections.IList list && _biCardKey != null && _biItemName != null)
                {
                    var arr = new NetEndlessCardRoll.RemovedItem[list.Count];
                    for (int i = 0; i < list.Count; i++)
                    {
                        object e = list[i]!;
                        arr[i] = new NetEndlessCardRoll.RemovedItem
                        {
                            CardKey = _biCardKey.GetValue(e) as string ?? "",
                            ItemName = _biItemName.GetValue(e) as string ?? "",
                        };
                    }
                    return arr;
                }
            }
            catch { }
            return Array.Empty<NetEndlessCardRoll.RemovedItem>();
        }

        private static void WriteRemovedItems(object fcm, NetEndlessCardRoll.RemovedItem[] items)
        {
            try
            {
                if (_removedItems?.GetValue(fcm) is not System.Collections.IList list || _biType == null || _biCardKey == null || _biItemName == null) return;
                list.Clear();
                if (items == null) return;
                foreach (var it in items)
                {
                    object entry = Activator.CreateInstance(_biType)!;
                    _biCardKey.SetValue(entry, it.CardKey ?? "");
                    _biItemName.SetValue(entry, it.ItemName ?? "");
                    list.Add(entry);
                }
            }
            catch { }
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
                _fcmCardsVisible = fcmType?.GetField("cardsVisible", bf);
                _fcmSpawnedCards = fcmType?.GetField("spawnedCards", bf);
                _fcmCardRewards  = fcmType?.GetField("cardRewards", bf);
                _fcmSpawnCards   = fcmType?.GetMethod("SpawnCards", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _fcmExitCards    = fcmType?.GetMethod("ExitCardSelection", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _fcIsStatic      = fcType?.GetField("isStaticCard", bf);
                _fcSubtitle      = fcType?.GetField("subtitleText", bf);
                _fcDescription   = fcType?.GetField("descriptionText", bf);
                _crCardKey       = crType?.GetField("cardKey", bf);

                var tmpType = _fcSubtitle?.FieldType;
                _tmpText = tmpType?.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);

                // EM-6b-2 roll-state replay reflection
                _emGameplayRandom = emType?.GetField("gameplayRandom", bf);
                _randState        = _emGameplayRandom?.FieldType.GetField("state", BindingFlags.Public | BindingFlags.Instance);
                _selOneTime       = fcmType?.GetField("selectedOneTimeUsed", bf);
                _banishedKeys     = fcmType?.GetField("banishedCardKeys", bf);
                _forcedPreselect  = fcmType?.GetField("forcedPreselectCardKeys", bf);
                _removedItems     = fcmType?.GetField("removedCardItems", bf);
                _banishesUsed     = fcmType?.GetField("banishesUsedThisSession", bf);
                _biType           = AccessTools.TypeByName("BanishedItemEntry") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.BanishedItemEntry");
                _biCardKey        = _biType?.GetField("cardKey", bf);
                _biItemName       = _biType?.GetField("itemName", bf);
                _emRerollsBF      = emType?.GetField("<RerollsRemaining>k__BackingField", bf);
                _emBanishesBF     = emType?.GetField("<BanishesRemaining>k__BackingField", bf);

                Plugin.Log.Info($"[Endless] EM-6b resolved cardMgr={_emCardManager != null} cardsSetup={_fcmCardsSetup != null} " +
                                $"spawned={_fcmSpawnedCards != null} rewards={_fcmCardRewards != null} isStatic={_fcIsStatic != null} " +
                                $"subtitle={_fcSubtitle != null} desc={_fcDescription != null} key={_crCardKey != null} tmpText={_tmpText != null} " +
                                $"spawnCards={_fcmSpawnCards != null} rnd={_emGameplayRandom != null} rndState={_randState != null} " +
                                $"selUsed={_selOneTime != null} banished={_banishedKeys != null} removed={_removedItems != null} biType={_biType != null} " +
                                $"rerollsBF={_emRerollsBF != null} banishesBF={_emBanishesBF != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-6b EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
