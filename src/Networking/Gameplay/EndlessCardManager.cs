using System;
using System.Collections;
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
        private static MethodInfo? _fcmInitSession;  // FloatingCardManager.InitializeSession() — rebuilds every card's item pool
        private static MethodInfo? _fcmSpinDismiss;  // FloatingCardManager.SpinAndDismissCard(int) : IEnumerator — the resolved-pick apply
        private static FieldInfo? _fcmCurrentSel;    // FloatingCardManager.currentSelectedIndex : int (the aimed card)
        private static FieldInfo? _fcmLastSelType;   // FloatingCardManager.lastSelectionType : CardSelectionType
        private static MethodInfo? _fcSetSelected;   // FloatingCard.SetSelected(CardSelectionType) — native border + enlarge
        private static object? _selCard, _selNone;   // CardSelectionType.Card / None (boxed) for the raffle highlight
        // manifest reconciliation (correct a diverged client roll back to the host's authoritative card set)
        private static FieldInfo? _fcmRewardDatabase; // FloatingCardManager.rewardDatabase : CardRewardDatabase
        private static MethodInfo? _crdGetSpecificCard; // CardRewardDatabase.GetSpecificCard(string) : CardReward
        private static MethodInfo? _fcSetCardType;    // FloatingCard.SetCardType(CardType)
        private static FieldInfo? _crCardType;        // CardReward.cardType
        private static FieldInfo? _fcPreselectedItem; // FloatingCard.preselectedItem : UnityEngine.Object
        private static FieldInfo? _crCustomArtwork;   // CardReward.customArtwork (the card's default icon)
        private static FieldInfo? _crCardLayout;      // CardReward.cardLayout
        private static MethodInfo? _fcSetArtwork;     // FloatingCard.SetArtwork(artwork, layout, qty, [addPadding])
        // EM-6b-3b: Skip/Reroll voting
        private static PropertyInfo? _fcInteractable; // FloatingCard.Interactable : bool (a 0-reroll card is non-interactable)
        private static MethodInfo? _fcStartSpin;      // FloatingCard.StartSpin() — client reroll visual feedback
        private static FieldInfo? _crEventType;       // CardReward.eventType : CardEventType
        private static object? _ceReroll;             // CardEventType.Reroll (boxed) — identifies the reroll card

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
        // Identity of the last-captured spawnedCards array. A new panel (including a chained reroll) always allocates a
        // fresh FloatingCard[] (FloatingCardManager.SetupCards), so comparing references detects it reliably — a reroll
        // flips cardsSetup false→true within one frame, so a "cardsSetup rising edge" poll can miss it entirely.
        private static object? _hostCapturedCards;
        private static int  _cardEventId;    // monotonic per host run

        // ---- client dedup / driven roll ----
        private static int    _clientLastEvent = -1;
        private static int    _clientRollEvent = -1;   // event id of the roll currently being replayed on the client
        private static int    _clientChoiceDrawOverride; // host ChoiceDrawAmount forced onto the client while replaying
        private static int    _clientExpectedEvent = -1; // manifest event whose keys the client should reproduce
        private static string _clientExpectedKeys = "";  // authoritative keys from that manifest
        private static int    _clientVerifiedEvent = -1;  // last event the client logged a roll-verify for
        private static NetEndlessCardManifest? _clientManifest; // full authoritative manifest for reconciliation
        private static int    _clientReconciledEvent = -1;  // last event whose diverged cards were corrected to the manifest

        /// <summary>CLIENT: true while a host-driven card roll is being replayed — used to force ChoiceDrawAmount parity and
        /// to block the client's own card pick (the pick becomes a vote in EM-6b-3).</summary>
        public static bool ClientRollActive { get; private set; }
        public static int  ClientChoiceDrawOverride => _clientChoiceDrawOverride;

        /// <summary>Set only while a shared-mode roll rebuilds its card pools, so the <c>CardReward.IsStorageUnlocked</c>
        /// patch forces every storage/service item into the pool identically on both ends (its real value is per-save, the
        /// main reason the client's replayed roll diverged). See <see cref="ResetCardPoolsForSharedRoll"/>.</summary>
        public static bool ForceStorageUnlocked { get; private set; }

        public static void Reset()
        {
            _hostCapturedCards = null;
            _cardEventId = 0;
            _clientLastEvent = -1;
            _clientRollEvent = -1;
            _clientExpectedEvent = -1;
            _clientExpectedKeys = "";
            _clientVerifiedEvent = -1;
            _clientManifest = null;
            _clientReconciledEvent = -1;
            ClientRollActive = false;
            EndlessCardVoteManager.Reset(); // EM-6b-3a: drop the previous run's card-vote state alongside the card state
        }

        /// <summary>HOST (Shared mode, each service tick): when a card panel is fully set up, capture the rolled cards once
        /// and broadcast them. The panel is identified by its <c>spawnedCards</c> array reference (a fresh array per panel),
        /// so a chained reroll — which flips <c>cardsSetup</c> false→true inside one frame, invisible to a rising-edge poll
        /// — is still detected as a new event.</summary>
        public static void HostTick()
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host || !SharedMode) return;
                EnsureResolved();
                object? fcm = ResolveCardManager();
                if (fcm == null) return;

                bool ready = _fcmCardsSetup?.GetValue(fcm) is bool b && b;
                if (!ready) return;                                     // no panel up (the next one is a fresh array)
                object? curCards = _fcmSpawnedCards?.GetValue(fcm);
                if (curCards == null) return;
                if (ReferenceEquals(curCards, _hostCapturedCards)) return; // already captured this exact panel

                var msg = BuildManifest(fcm);
                if (msg == null) return; // arrays not populated yet this frame — retry next tick (still not captured)
                _hostCapturedCards = curCards;

                NetGameplaySyncBridge.BroadcastHostEndlessCardManifest(msg);
                ApplyManifest(msg); // the host keeps the same authoritative record (mirror UI + vote build on it in EM-6b-2/3)
                if (LogOn) Plugin.Log.Info($"[Endless] EM-6b host card manifest event={msg.CardEventId} count={msg.Cards.Length} keys=[{KeysOf(msg)}]");

                // EM-6b-3b: open the shared 1-of-N card vote for this panel. Every interactable card is votable —
                // the ordinary reward cards plus the static Skip/Reroll (a 0-reroll card is non-interactable and excluded).
                int[] votable = GetVotableIndices(fcm);
                EndlessCardVoteManager.HostOpenVote(msg.CardEventId, msg.Cards.Length, votable, msg.ChapterName, msg.LevelIndex, msg.HasLevelSeed, msg.LevelSeed);
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

                ResetCardPoolsForSharedRoll(fcm); // canonical, unlock-neutral pools so the client's replay is deterministic

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

                ResetCardPoolsForSharedRoll(fcm); // match the host: canonical, unlock-neutral pools before writing state

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
                if (!match) ClientReconcileToManifest(fcm); // correct the diverged cards to the host's authoritative set
            }
            catch { }
        }

        /// <summary>CLIENT: the replayed roll diverged from the host's set (per-player card state — e.g. unlock-gated
        /// storage/service pools — breaks the RNG determinism). Force each ordinary card's identity to the host manifest
        /// (correct <c>CardReward</c> by key + host-rendered title/desc), so the vote and the applied reward always match
        /// the host. Artwork / preselected items may still differ on a corrected card — within the accepted tolerance.</summary>
        private static void ClientReconcileToManifest(object fcm)
        {
            try
            {
                if (_clientManifest == null || _clientManifest.CardEventId != _clientRollEvent) return;
                if (_clientReconciledEvent == _clientRollEvent) return;
                if (_fcmSpawnedCards?.GetValue(fcm) is not Array cards) return;
                if (_fcmCardRewards?.GetValue(fcm) is not Array rewards) return;
                object? db = _fcmRewardDatabase?.GetValue(fcm);
                if (db == null || _crdGetSpecificCard == null) return;

                _clientReconciledEvent = _clientRollEvent;
                int corrected = 0;
                var entries = _clientManifest.Cards;
                int n = Math.Min(Math.Min(cards.Length, rewards.Length), entries.Length);
                for (int i = 0; i < n; i++)
                {
                    if (entries[i].IsStatic) continue; // Skip/Reroll cards are fixed prefabs, never re-keyed
                    string want = entries[i].Key ?? "";
                    object? reward = rewards.GetValue(i);
                    string have = reward != null ? (_crCardKey?.GetValue(reward) as string ?? "") : "";
                    if (string.Equals(have, want, StringComparison.Ordinal)) continue; // already correct

                    object? corrReward = _crdGetSpecificCard.Invoke(db, new object[] { want });
                    object? card = cards.GetValue(i);
                    if (corrReward == null || card == null) continue;
                    rewards.SetValue(corrReward, i);
                    try { if (_crCardType != null && _fcSetCardType != null) _fcSetCardType.Invoke(card, new[] { _crCardType.GetValue(corrReward) }); } catch { }
                    SetTmp(card, _fcSubtitle, entries[i].Title);
                    SetTmp(card, _fcDescription, entries[i].Desc);
                    ApplyReconcileArtwork(card, corrReward);
                    try { _fcPreselectedItem?.SetValue(card, null); } catch { } // re-derived at apply from the correct reward's pool
                    corrected++;
                }
                if (LogOn || corrected > 0) Plugin.Log.Info($"[Endless] EM-6b-3a client reconciled {corrected} diverged card(s) to host manifest event={_clientRollEvent}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ClientReconcileToManifest failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Best-effort: give a reconciled card its reward's default icon (<c>customArtwork</c>). Exact for buff /
        /// resource cards; a diverged preselected-item card (loot/summon) shows the generic reward art rather than the
        /// host's specific item icon — within the accepted art tolerance, and far better than a wrong card's icon.</summary>
        private static void ApplyReconcileArtwork(object card, object reward)
        {
            try
            {
                if (_fcSetArtwork == null || _crCustomArtwork == null || _crCardLayout == null) return;
                object? art = _crCustomArtwork.GetValue(reward);
                if (art is UnityEngine.Object auo && auo == null) art = null;
                if (art == null) return; // no default art → leave the rolled icon rather than blank the card
                object? layout = _crCardLayout.GetValue(reward);
                var ps = _fcSetArtwork.GetParameters();
                var args = new object?[ps.Length];
                args[0] = art;
                if (ps.Length > 1) args[1] = layout;
                if (ps.Length > 2) args[2] = "";
                for (int a = 3; a < ps.Length; a++)
                    args[a] = ps[a].HasDefaultValue ? ps[a].DefaultValue
                            : ps[a].ParameterType.IsValueType ? Activator.CreateInstance(ps[a].ParameterType) : null;
                _fcSetArtwork.Invoke(card, args);
            }
            catch { }
        }

        private static void SetTmp(object? card, FieldInfo? tmpField, string text)
        {
            try
            {
                if (card == null || tmpField == null || _tmpText == null) return;
                object? tmp = tmpField.GetValue(card);
                if (tmp is UnityEngine.Object uo && uo == null) return;
                if (tmp != null) _tmpText.SetValue(tmp, text ?? "");
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

        // ================================================================== EM-6b-3a card-vote helpers (both roles)

        /// <summary>True while the local card panel is set up (cards spawned, not yet torn down). Used by the vote manager
        /// to detect the end of the event (SpinAndDismissCard clears cardsSetup when it finishes).</summary>
        public static bool LocalCardPanelUp()
        {
            try { EnsureResolved(); object? fcm = ResolveCardManager(); return fcm != null && _fcmCardsSetup?.GetValue(fcm) is bool b && b; }
            catch { return false; }
        }

        /// <summary>Read the votable card the local player is currently aiming at + firing on. A votable card is any
        /// <b>interactable</b> card — ordinary reward, Skip, or an available Reroll (EM-6b-3b). Returns false for no
        /// selection, a dismiss-button (banish, EM-6b-3c) selection, or a non-interactable card (e.g. a 0-reroll card).</summary>
        public static bool TryReadAimedVotableCard(object fcm, int cardCount, out int index)
        {
            index = -1;
            try
            {
                if (fcm == null) return false;
                int sel = _fcmCurrentSel?.GetValue(fcm) is int s ? s : -1;
                int selType = _fcmLastSelType != null ? Convert.ToInt32(_fcmLastSelType.GetValue(fcm)) : 0;
                // CardSelectionType: 1 = Card, 2 = SelectButton (a pick); 3 = DismissButton (banish — deferred).
                if (sel < 0 || sel >= cardCount) return false;
                if (selType != 1 && selType != 2) return false;
                if (_fcmSpawnedCards?.GetValue(fcm) is Array cards && sel < cards.Length)
                {
                    object? card = cards.GetValue(sel);
                    if (card == null) return false;
                    if (!IsInteractable(card)) return false; // exhausted 0-reroll card can't be voted
                }
                index = sel;
                return true;
            }
            catch { return false; }
        }

        /// <summary>The interactable card indices in the current panel (ordinary + Skip + an available Reroll) — the votable
        /// set. A 0-reroll card reports <c>Interactable == false</c> and is excluded, so no host tie/no-vote roll can land on
        /// a dead reroll.</summary>
        private static int[] GetVotableIndices(object fcm)
        {
            var list = new System.Collections.Generic.List<int>();
            try
            {
                if (_fcmSpawnedCards?.GetValue(fcm) is Array cards)
                    for (int i = 0; i < cards.Length; i++)
                    {
                        object? card = cards.GetValue(i);
                        if (card is UnityEngine.Object uo && uo != null && IsInteractable(card)) list.Add(i);
                    }
            }
            catch { }
            return list.ToArray();
        }

        private static bool IsInteractable(object card)
        {
            try { return _fcInteractable == null || (_fcInteractable.GetValue(card) is bool b && b); }
            catch { return true; }
        }

        /// <summary>True if the card at <paramref name="index"/> is the Reroll card (host-authoritative re-roll on resolve).</summary>
        private static bool IsRerollCard(object fcm, int index)
        {
            try
            {
                if (_ceReroll == null || _crEventType == null) return false;
                if (_fcmCardRewards?.GetValue(fcm) is not Array rewards || index < 0 || index >= rewards.Length) return false;
                object? reward = rewards.GetValue(index);
                if (reward == null) return false;
                return Equals(_crEventType.GetValue(reward), _ceReroll);
            }
            catch { return false; }
        }

        /// <summary>Describe the resolved card for the status HUD: 0 = ordinary, 1 = Skip, 2 = Reroll. Reads the local card
        /// manager so the wire carries no extra field.</summary>
        public static int DescribeResolvedCard(int index)
        {
            try
            {
                EnsureResolved();
                object? fcm = ResolveCardManager();
                if (fcm == null) return 0;
                if (_fcmSpawnedCards?.GetValue(fcm) is not Array cards || index < 0 || index >= cards.Length) return 0;
                object? card = cards.GetValue(index);
                if (card == null || !(_fcIsStatic?.GetValue(card) is bool st && st)) return 0; // ordinary
                return IsRerollCard(fcm, index) ? 2 : 1; // static → Reroll or Skip
            }
            catch { return 0; }
        }

        /// <summary>BOTH ROLES: apply the vote-resolved card. For an ordinary or Skip card, run the vanilla
        /// <c>SpinAndDismissCard(index)</c> on both ends — the reward's personal effect lands on each player's own
        /// <c>PlayerUnit</c> (world-card duplication is the known EM-7 gap). For a <b>Reroll</b> (EM-6b-3b) the re-roll is
        /// host-authoritative: the host runs the vanilla spin, whose terminal <c>SpawnCards</c> re-rolls the panel and
        /// broadcasts a fresh roll/manifest/vote; the client does <b>not</b> run the spin (its terminal <c>SpawnCards</c>
        /// would roll a divergent local panel and could collide with the incoming host roll mid-coroutine) — instead it
        /// spins the reroll card for feedback and waits for the host's new panel, which replaces its cards via
        /// <see cref="ApplyCardRoll"/>.</summary>
        public static void ApplyResolvedPick(int index)
        {
            try
            {
                EnsureResolved();
                object? fcm = ResolveCardManager();
                if (fcm == null || _fcmSpinDismiss == null) return;
                if (_fcmSpawnedCards?.GetValue(fcm) is not Array cards || index < 0 || index >= cards.Length) return;
                if (cards.GetValue(index) == null) return; // card already gone

                if (IsRerollCard(fcm, index) && NetGameplaySyncBridge.BossMode == NetMode.Client)
                {
                    // Client reroll: host owns the re-roll. Spin the card for feedback, keep ClientRollActive set so the
                    // incoming host roll's TeardownClientCards fires and swaps in the authoritative new panel.
                    try { if (cards.GetValue(index) is object rc) _fcStartSpin?.Invoke(rc, null); } catch { }
                    return;
                }

                ClientRollActive = false; // client (ordinary/Skip): the spin tears the panel down; keep the edge teardown from double-firing
                if (_fcmSpinDismiss.Invoke(fcm, new object[] { index }) is IEnumerator e && fcm is MonoBehaviour mb)
                    mb.StartCoroutine(e);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] ApplyResolvedPick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Drive the vanilla card highlight (border + enlarge, via <c>FloatingCard.SetSelected</c> → the card's own
        /// scale lerp) so the tie-break raffle sweep reuses the native selection look. Called from a
        /// <c>FloatingCardManager.Update</c> postfix while a raffle is active, so it overrides the frame's aim-based
        /// highlight. A localScale write here would be futile — the card's own Update overwrites localScale every frame.</summary>
        public static void ApplyRaffleHighlight(object fcm, int cursor)
        {
            try
            {
                EnsureResolved();
                if (fcm == null || _fcSetSelected == null || _selCard == null || _selNone == null) return;
                if (_fcmSpawnedCards?.GetValue(fcm) is not Array cards) return;
                for (int i = 0; i < cards.Length; i++)
                {
                    object? card = cards.GetValue(i);
                    if (card is not UnityEngine.Object uo || uo == null) continue;
                    _fcSetSelected.Invoke(card, new[] { i == cursor ? _selCard : _selNone });
                }
            }
            catch { }
        }

        // ---- stamp-UI accessors (EM-6b-3a on-card voter stamps) reuse this class's cached reflection ----
        public static object? ResolveLocalCardManager() { EnsureResolved(); return ResolveCardManager(); }
        public static Array?  GetSpawnedCards(object fcm) { try { return _fcmSpawnedCards?.GetValue(fcm) as Array; } catch { return null; } }
        public static Component? GetCardSubtitle(object card) { try { return _fcSubtitle?.GetValue(card) as Component; } catch { return null; } }
        public static bool CardIsStatic(object card) { try { return _fcIsStatic?.GetValue(card) is bool b && b; } catch { return false; } }

        /// <summary>Rebuild every card's item pool to a canonical, unlock-neutral state right before a shared-mode roll, on
        /// BOTH ends, so the roll is deterministic and the client reproduces the host's cards natively (correct language +
        /// artwork) — no post-roll correction needed. The per-save storage/service unlock filter (the main divergence
        /// source) is forced open via <see cref="ForceStorageUnlocked"/>; stale/empty pools are refreshed. Only touches
        /// pools (never selectedOneTimeUsed / banished lists, which the roll state carries).</summary>
        private static void ResetCardPoolsForSharedRoll(object fcm)
        {
            if (_fcmInitSession == null) return;
            try
            {
                ForceStorageUnlocked = true;
                try { _fcmInitSession.Invoke(fcm, null); }
                finally { ForceStorageUnlocked = false; }
            }
            catch (Exception ex) { ForceStorageUnlocked = false; Plugin.Log.Warn($"[Endless] ResetCardPoolsForSharedRoll failed: {ex.GetType().Name}: {ex.Message}"); }
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
                    _clientManifest = msg;                          // EM-6b-3a: kept so a diverged roll can be corrected to it
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
                _fcmInitSession  = fcmType?.GetMethod("InitializeSession", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _fcmSpinDismiss  = fcmType?.GetMethod("SpinAndDismissCard", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                _fcmCurrentSel   = fcmType?.GetField("currentSelectedIndex", bf);
                _fcmLastSelType  = fcmType?.GetField("lastSelectionType", bf);
                _fcIsStatic      = fcType?.GetField("isStaticCard", bf);
                _fcSubtitle      = fcType?.GetField("subtitleText", bf);
                _fcDescription   = fcType?.GetField("descriptionText", bf);
                _crCardKey       = crType?.GetField("cardKey", bf);

                var cstType = AccessTools.TypeByName("CardSelectionType") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.CardSelectionType");
                if (cstType != null)
                {
                    _fcSetSelected = fcType?.GetMethod("SetSelected", BindingFlags.Public | BindingFlags.Instance, null, new[] { cstType }, null);
                    try { _selCard = Enum.ToObject(cstType, 1); _selNone = Enum.ToObject(cstType, 0); } catch { }
                }

                _fcmRewardDatabase = fcmType?.GetField("rewardDatabase", bf);
                var crdbType = _fcmRewardDatabase?.FieldType;
                _crdGetSpecificCard = crdbType?.GetMethod("GetSpecificCard", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                _crCardType = crType?.GetField("cardType", bf);
                _fcPreselectedItem = fcType?.GetField("preselectedItem", bf);
                if (_crCardType != null)
                    _fcSetCardType = fcType?.GetMethod("SetCardType", BindingFlags.Public | BindingFlags.Instance, null, new[] { _crCardType.FieldType }, null);
                _crCustomArtwork = crType?.GetField("customArtwork", bf);
                _crCardLayout    = crType?.GetField("cardLayout", bf);
                if (_crCustomArtwork != null && fcType != null)
                {
                    // Pick the SetArtwork overload whose first parameter accepts customArtwork's type (Sprite/Texture).
                    foreach (var m in fcType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "SetArtwork") continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType.IsAssignableFrom(_crCustomArtwork.FieldType)) { _fcSetArtwork = m; break; }
                    }
                }

                // EM-6b-3b: Skip/Reroll voting — interactability (excludes a 0-reroll card), reroll detection, spin feedback.
                _fcInteractable = fcType?.GetProperty("Interactable", BindingFlags.Public | BindingFlags.Instance);
                _fcStartSpin    = fcType?.GetMethod("StartSpin", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _crEventType    = crType?.GetField("eventType", bf);
                var ceType = _crEventType?.FieldType ?? AccessTools.TypeByName("CardEventType") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.CardEventType");
                if (ceType != null && ceType.IsEnum) { try { _ceReroll = Enum.Parse(ceType, "Reroll"); } catch { _ceReroll = null; } }

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
