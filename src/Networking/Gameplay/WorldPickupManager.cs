using System;
using System.Collections.Generic;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.CharacterStats;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.UI;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// World item-drop sync — player-thrown items first (forward-compatible with a future Shared-loot host setting that
    /// flips the filter to "all pickups"). See <see cref="NetWorldPickupSpawn"/> for the model.
    ///
    /// <para><b>Spawn</b> = optimistic + peer-authoritative: the dropping peer's real pickup appears instantly; we assign
    /// a composite <c>{ownerPeer,seq}</c> id and broadcast it; receivers mirror-spawn the same pickup (with the same
    /// DIY <c>InventoryData</c>) and register it under that id.</para>
    ///
    /// <para><b>Take</b> = host-authoritative first-come-wins: a synced pickup's local <c>ExecutePickup</c> is blocked;
    /// instead a client asks the host (<see cref="NetWorldPickupTake"/>) and the host grants exactly one, broadcasting a
    /// removal (<see cref="NetWorldPickupRemoved"/>) so the item vanishes everywhere and only the winner receives it.</para>
    ///
    /// <para>All game-object mutation (AddItem / remove-from-world) is deferred to <see cref="Tick"/> so it never runs
    /// inside the game's <c>InteractionManager.Update</c> list iteration (the take is triggered from an ExecutePickup
    /// prefix).</para>
    /// </summary>
    internal static class WorldPickupManager
    {
        private sealed class Entry
        {
            public string OwnerPeerId = "";
            public ushort Seq;
            public string Key = "";
            public ItemDefinition ItemSO;
            public InventoryData Data;   // game InventoryData (null for loot)
            public bool Rotated;

            // WID-2 rest-position sync (owner side only). We authoritatively settle our own drops and broadcast the
            // rest position once so mirrors — whose independent physics + co-op no-pause collisions diverge — snap to it.
            public bool    IsLocalOwner;
            public float   SpawnTime;
            public float   RestSince = -1f;   // when the body last began resting (-1 = moving)
            public bool    SettleSent;
            public Vector3 LastSettlePos;

            // Mirror side only: local cosmetic glide to the owner's authoritative rest position instead of snapping there.
            public bool    Animating;
            public Vector3 AnimFrom;
            public Vector3 AnimTo;
            public float   AnimStart;
            public float   AnimDuration;
            public float   AnimArc;
        }

        // Live synced pickups, both directions of lookup.
        private static readonly Dictionary<Pickup, Entry> _byPickup = new Dictionary<Pickup, Entry>();
        private static readonly Dictionary<string, Pickup> _byKey   = new Dictionary<string, Pickup>();

        // Host-only: keys already granted to a taker (enforce first-come-wins).
        private static readonly HashSet<string> _claimed = new HashSet<string>();

        // All peers: keys that have been removed — so a late/out-of-order spawn for a taken pickup is not re-created.
        private static readonly HashSet<string> _deadKeys = new HashSet<string>();

        // Client-only: per-key take-request cooldown (avoid spamming on the auto-pickup / hold-interact path).
        private static readonly Dictionary<string, float> _requestCooldown = new Dictionary<string, float>();
        private const float RequestCooldownSeconds = 1.0f;

        // Pending removals to apply on the next main-thread Tick (decoupled from any game iteration).
        private struct PendingRemoval { public string Key; public string TakenBy; }
        private static readonly List<PendingRemoval> _pendingRemovals = new List<PendingRemoval>();

        private static bool   _applyingMirror; // true while WE mirror-spawn — the SpawnPickup postfix must not re-broadcast
        private static ushort _seq;

        public static bool IsApplyingMirror => _applyingMirror;

        /// <summary>EM-7: set by the HOST only, for the duration of an Endless card's loot-table reward
        /// (<c>FloatingCardManager.ExecuteReward</c> → <c>SpawnFromLootTable</c> → <c>SpawnPickup</c>). In Shared-endless
        /// mode the client suppresses its own copy of that reward, so the host's pickup must be mirrored to the client
        /// <b>independently of the SharedLoot toggle</b> — otherwise the shared card loot would only exist on the host.
        /// Scoped tightly to the host's synchronous loot spawn so no other pickup qualifies through it.</summary>
        public static bool EndlessSharedLootContext;

        // ----------------------------------------------------------------- filter (mode-driven)

        private static bool Qualifies(Pickup p)
        {
            if (p == null || p.ItemSO == null) return false;
            if (EndlessSharedLootContext) return true;        // EM-7: host-authoritative Endless card loot (mode-independent)
            if (NetSessionSettings.SharedLootEnabled) return true;   // SL-4 Shared-loot mode (host-authoritative): every pickup
            return p.inventoryData != null;                   // Independent mode: only player drops (Pickup.DroppedByPlayer)
        }

        // ----------------------------------------------------------------- spawn (local → broadcast)

        public static void CaptureLocalSpawn(Pickup p)
        {
            try
            {
                if (_applyingMirror) return;                            // our own mirror — already networked
                if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;
                if (p == null) return;
                if (_byPickup.ContainsKey(p)) return;                  // already tracked
                if (!Qualifies(p)) return;

                string owner = NetGameplaySyncBridge.LocalPeerId;
                if (string.IsNullOrEmpty(owner)) return;

                ushort seq = ++_seq;
                var msg = BuildSpawnMessage(p, owner, seq);
                Register(p, owner, seq, p.ItemSO, p.inventoryData, p.inventoryData != null && p.inventoryData.rotated);

                NetGameplaySyncBridge.ReportLocalWorldPickupSpawn(msg);

                if (Plugin.Cfg.LogWorldItemDropSync.Value)
                    NetLogger.Info($"[WorldPickup] capture key={msg.Key} item={p.ItemSO.name} hasData={msg.HasData} pos={msg.Position}");
            }
            catch (Exception ex) { NetLogger.Warn($"[WorldPickup] capture failed: {ex.Message}"); }
        }

        public static void ApplyRemoteSpawn(NetWorldPickupSpawn m)
        {
            try
            {
                if (!Plugin.Cfg.EnableWorldItemDropSync.Value || m == null) return;
                if (_byKey.ContainsKey(m.Key)) return;   // already mirrored
                if (_deadKeys.Contains(m.Key)) return;   // removal already arrived — don't resurrect

                ItemDatabase db = AsyncAssetLoading.Instance != null ? AsyncAssetLoading.Instance.itemDatabase : null;
                ItemDefinition item = db != null ? db[new ItemId(m.ItemId)] : null;
                if (item == null)
                {
                    if (Plugin.Cfg.LogWorldItemDropSync.Value)
                        NetLogger.Warn($"[WorldPickup] mirror key={m.Key} unknown itemId={m.ItemId}");
                    return;
                }

                InventoryData data = m.HasData ? BuildInventoryData(m, item) : null;

                Pickup spawned = null;
                _applyingMirror = true;
                try { spawned = StaticInstance<InteractionManager>.Instance.SpawnPickup(m.Position, false, item, null, data); }
                finally { _applyingMirror = false; }

                if (spawned != null)
                {
                    Register(spawned, m.OwnerPeerId, m.Seq, item, data, m.HasData && m.Rotated);
                    if (Plugin.Cfg.LogWorldItemDropSync.Value)
                        NetLogger.Info($"[WorldPickup] mirror key={m.Key} item={item.name} hasData={m.HasData}");
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[WorldPickup] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- take (ExecutePickup prefix)

        /// <summary>Returns true if this pickup is synced and the local vanilla pickup must be blocked (we handle it via
        /// host arbitration). Returns false for un-synced pickups (loot in Independent mode) — let vanilla run.</summary>
        public static bool TryBeginTake(Pickup p)
        {
            try
            {
                if (!Plugin.Cfg.EnableWorldItemDropSync.Value || p == null) return false;
                if (!_byPickup.TryGetValue(p, out var e)) return false;   // not synced → vanilla pickup
                if (!NetGameplaySyncBridge.IsSessionActive) return false;

                // Fit pre-check (reduce item loss on a contested grab). On grant we still use AddItem's real result.
                if (!HasRoomFor(e.ItemSO)) return true;                   // block; behaves like vanilla "didn't fit"

                if (NetGameplaySyncBridge.IsHost)
                    HostGrant(e.OwnerPeerId, e.Seq, NetGameplaySyncBridge.LocalPeerId);
                else
                    TrySendTakeRequest(e);

                return true; // always block the local vanilla pickup for synced items
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[WorldPickup] begin-take failed: {ex.Message}");
                return false;
            }
        }

        private static void TrySendTakeRequest(Entry e)
        {
            float now = Time.realtimeSinceStartup;
            if (_requestCooldown.TryGetValue(e.Key, out var t) && now < t) return;
            _requestCooldown[e.Key] = now + RequestCooldownSeconds;
            NetGameplaySyncBridge.SendWorldPickupTakeRequest(e.OwnerPeerId, e.Seq);
            if (Plugin.Cfg.LogWorldItemDropSync.Value)
                NetLogger.Info($"[WorldPickup] take request key={e.Key}");
        }

        // ----------------------------------------------------------------- host arbitration

        /// <summary>Host: a client asked to take a pickup. Grant to the first valid requester.</summary>
        public static void HostHandleTakeRequest(NetWorldPickupTake req, string requesterPeerId)
        {
            if (req == null || string.IsNullOrEmpty(requesterPeerId)) return;
            HostGrant(req.OwnerPeerId, req.Seq, requesterPeerId);
        }

        private static void HostGrant(string ownerPeerId, ushort seq, string takerPeerId)
        {
            string key = ownerPeerId + "#" + seq;
            if (!_claimed.Add(key)) return;   // someone already won this one

            var rm = new NetWorldPickupRemoved { OwnerPeerId = ownerPeerId, Seq = seq, TakenByPeerId = takerPeerId };
            NetGameplaySyncBridge.BroadcastWorldPickupRemoved(rm);  // → clients
            EnqueueRemoval(key, takerPeerId);                       // local (host)

            if (Plugin.Cfg.LogWorldItemDropSync.Value)
                NetLogger.Info($"[WorldPickup] host grant key={key} taker={takerPeerId}");
        }

        // ----------------------------------------------------------------- removal (deferred to Tick)

        public static void ApplyRemoved(string key, string takenBy)
        {
            EnqueueRemoval(key, takenBy);
        }

        private static void EnqueueRemoval(string key, string takenBy)
        {
            if (string.IsNullOrEmpty(key)) return;
            _deadKeys.Add(key);
            _requestCooldown.Remove(key);
            _pendingRemovals.Add(new PendingRemoval { Key = key, TakenBy = takenBy ?? "" });
        }

        /// <summary>Main-thread drain — applies queued removals (AddItem for the taker + remove-from-world) safely
        /// outside any game-loop iteration, and drives the owner-side rest-position sync. Called each frame from
        /// NetService.</summary>
        public static void Tick()
        {
            UpdateSettleTracking();
            AdvanceSettleAnimations();

            if (_pendingRemovals.Count == 0) return;
            // Snapshot + clear first so a re-entrant enqueue (shouldn't happen) doesn't loop.
            PendingRemoval[] batch = _pendingRemovals.ToArray();
            _pendingRemovals.Clear();

            string localPeer = NetGameplaySyncBridge.LocalPeerId;
            foreach (var r in batch)
            {
                try { ApplyRemovalNow(r.Key, r.TakenBy, localPeer); }
                catch (Exception ex) { NetLogger.Warn($"[WorldPickup] removal failed key={r.Key}: {ex.Message}"); }
            }
        }

        private static void ApplyRemovalNow(string key, string takenBy, string localPeer)
        {
            if (!_byKey.TryGetValue(key, out var p) || p == null)
            {
                if (Plugin.Cfg.LogWorldItemDropSync.Value)
                    NetLogger.Info($"[WorldPickup] removed key={key} (no local instance)");
                _byKey.Remove(key);
                return;
            }

            bool iAmTaker = !string.IsNullOrEmpty(takenBy) && takenBy == localPeer;
            Entry e = _byPickup.TryGetValue(p, out var ee) ? ee : null;

            if (iAmTaker && e != null && e.ItemSO != null)
            {
                ItemGrid grid = StaticInstance<UIManager>.Instance != null
                    ? StaticInstance<UIManager>.Instance.PlayerBackpackGrid : null;
                bool ok = grid != null && grid.AddItem(e.ItemSO, true, true, e.Data);
                if (!ok)
                    NetLogger.Warn($"[WorldPickup] taker bag full — item '{e.ItemSO.name}' lost (key={key})");
                else if (Plugin.Cfg.LogWorldItemDropSync.Value)
                    NetLogger.Info($"[WorldPickup] taker received '{e.ItemSO.name}' (key={key})");
            }

            RemoveWorldPickup(p);
            UnregisterPickup(p);
        }

        // ----------------------------------------------------------------- rest-position sync (WID-2)

        private const float RestVelSqr      = 0.0025f; // ~0.05 m/s — treat below this as "at rest" (game freezes at 0.001)
        private const float RestDebounce    = 0.4f;    // must stay at rest this long before we trust the position
        private const float SettleTimeout   = 8f;      // hard cap: broadcast a position even if it never fully settles
        private const float ResettleMoveSqr = 0.09f;   // 0.3 m — if a settled drop is later shoved this far, re-sync it

        /// <summary>Owner side: watch each pickup we dropped, and once it comes to rest broadcast its authoritative
        /// position so every mirror snaps to the same spot (their independent physics otherwise diverge — the more so in
        /// co-op's no-pause bag dumps where drops collide and scatter locally).</summary>
        private static void UpdateSettleTracking()
        {
            if (_byPickup.Count == 0) return;
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            if (!NetGameplaySyncBridge.IsSessionActive) return;

            float now = Time.time;
            foreach (var kv in _byPickup)
            {
                Pickup p = kv.Key; Entry e = kv.Value;
                if (!e.IsLocalOwner || p == null) continue;

                var body = p.body;
                Vector3 pos = p.transform.position;
                bool atRest = body == null || body.isKinematic || body.linearVelocity.sqrMagnitude < RestVelSqr;

                if (!e.SettleSent)
                {
                    if (atRest) { if (e.RestSince < 0f) e.RestSince = now; }
                    else        { e.RestSince = -1f; }

                    bool settledLongEnough = e.RestSince >= 0f && now - e.RestSince >= RestDebounce;
                    bool timedOut          = now - e.SpawnTime >= SettleTimeout;
                    if (settledLongEnough || timedOut)
                    {
                        SendSettle(e, pos);
                        e.SettleSent = true;
                        e.LastSettlePos = pos;
                        e.RestSince = -1f;
                    }
                }
                else if ((pos - e.LastSettlePos).sqrMagnitude > ResettleMoveSqr)
                {
                    // Disturbed after settling (explosion / kicked by a later drop) — let it re-settle and re-sync.
                    e.SettleSent = false;
                    e.RestSince = -1f;
                }
            }
        }

        private static void SendSettle(Entry e, Vector3 pos)
        {
            NetGameplaySyncBridge.ReportLocalWorldPickupSettle(
                new NetWorldPickupSettle { OwnerPeerId = e.OwnerPeerId, Seq = e.Seq, Position = pos });
            if (Plugin.Cfg.LogWorldItemDropSync.Value)
                NetLogger.Info($"[WorldPickup] settle key={e.Key} pos={pos}");
        }

        // Mirror-side settle glide (WID-3): the correction from the mirror's independently-simulated rest spot to the
        // owner's authoritative one used to snap instantly (a visible teleport). Instead we ease the pickup there over a
        // short, distance-scaled window with a small hop arc — cosmetic, local, driven from Tick. Not uniform/linear.
        private const float SettleAnimMinDistance = 0.05f; // below this a glide is imperceptible → snap (and skip the work)
        private const float SettleAnimSpeed       = 6f;    // reference glide speed (m/s) → duration = distance / speed
        private const float SettleAnimMinDuration = 0.12f;
        private const float SettleAnimMaxDuration = 0.5f;  // cap so a large correction never crawls
        private const float SettleAnimArcRatio    = 0.25f; // hop height as a fraction of glide distance
        private const float SettleAnimMaxArc      = 0.4f;  // cap the hop so it never launches oddly far on a big correction

        /// <summary>Mirror side: move a synced pickup to the owner's authoritative rest position and freeze it (matching
        /// what the game itself does to a landed pickup), so it can no longer drift from the owner's copy. The move is a
        /// short local glide (see <see cref="AdvanceSettleAnimations"/>) rather than a snap; the body is frozen up front
        /// so physics can't fight the glide, and we drive the transform until it reaches the target.</summary>
        public static void ApplySettle(string key, Vector3 pos)
        {
            try
            {
                if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
                if (!_byKey.TryGetValue(key, out var p) || p == null) return; // unknown / already taken → ignore
                if (!_byPickup.TryGetValue(p, out var e)) { SetPickupPosition(p, pos); return; }
                if (e.IsLocalOwner) return; // never move our own authoritative drop

                // Freeze now (kinematic, no collisions) — the same end-state the game gives a landed pickup — so the
                // remaining physics can't drag against the glide. We then own the transform until it reaches pos.
                var body = p.body;
                if (body != null)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                    body.detectCollisions = false;
                }

                Vector3 from = p.transform.position;
                float dist = Vector3.Distance(from, pos);
                if (dist < SettleAnimMinDistance)
                {
                    e.Animating = false;
                    SetPickupPosition(p, pos); // negligible correction — snap
                }
                else
                {
                    e.Animating    = true;
                    e.AnimFrom     = from;
                    e.AnimTo       = pos;
                    e.AnimStart    = Time.time;
                    e.AnimDuration = Mathf.Clamp(dist / SettleAnimSpeed, SettleAnimMinDuration, SettleAnimMaxDuration);
                    e.AnimArc      = Mathf.Min(dist * SettleAnimArcRatio, SettleAnimMaxArc);
                }

                if (Plugin.Cfg.LogWorldItemDropSync.Value)
                    NetLogger.Info($"[WorldPickup] mirror settle key={key} pos={pos} dist={dist:0.00} anim={e.Animating}");
            }
            catch (Exception ex) { NetLogger.Warn($"[WorldPickup] settle apply failed: {ex.Message}"); }
        }

        /// <summary>Advance any in-flight mirror settle glides (main-thread, from <see cref="Tick"/>). Ease-out on the
        /// horizontal path with a small sine hop that peaks mid-glide and returns to zero at the target, so the item
        /// reads as gliding into place rather than teleporting. Purely cosmetic; no structural change to the registry,
        /// so iterating it here is safe.</summary>
        private static void AdvanceSettleAnimations()
        {
            if (_byPickup.Count == 0) return;
            float now = Time.time;
            foreach (var kv in _byPickup)
            {
                Entry e = kv.Value;
                if (!e.Animating) continue;
                Pickup p = kv.Key;
                if (p == null) { e.Animating = false; continue; }

                float t = e.AnimDuration <= 0f ? 1f : Mathf.Clamp01((now - e.AnimStart) / e.AnimDuration);
                float eased = EaseOutCubic(t);
                Vector3 pos = Vector3.Lerp(e.AnimFrom, e.AnimTo, eased);
                pos.y += e.AnimArc * Mathf.Sin(Mathf.PI * t); // hop: 0 at start, peak at mid, back to 0 at the target
                SetPickupPosition(p, pos);

                if (t >= 1f)
                {
                    SetPickupPosition(p, e.AnimTo);
                    e.Animating = false;
                }
            }
        }

        private static void SetPickupPosition(Pickup p, Vector3 pos)
        {
            if (p == null) return;
            var body = p.body;
            if (body != null) body.position = pos;
            p.transform.position = pos;
        }

        private static float EaseOutCubic(float t)
        {
            float u = 1f - t;
            return 1f - u * u * u;
        }

        // ----------------------------------------------------------------- spawn separation (WID-2, anti-tower)

        private struct RecentSpawn { public Vector3 Pos; public float Time; }
        private static readonly List<RecentSpawn> _recentSpawns = new List<RecentSpawn>();
        private static int _separationFan;

        private const float SeparationRadiusSqr = 0.25f; // 0.5 m — only nudge a drop that lands amid recent ones
        private const float SeparationWindow    = 1.5f;  // how long a recent spawn still counts as a neighbour
        private const float SeparationImpulse   = 0.7f;  // gentle horizontal push (m/s, VelocityChange like the vanilla throw)

        /// <summary>Give a drop that lands amid other recent drops a small horizontal shove away from them, so co-op
        /// bag dumps fan out on the ground instead of stacking into one indistinguishable tower. Local + cosmetic:
        /// for a synced player drop the owner's <see cref="UpdateSettleTracking"/> re-syncs the resulting rest position,
        /// so all peers still converge. Left untouched in single-player (the game pauses there and drops behave).</summary>
        public static void ApplySpawnSeparation(Pickup p)
        {
            try
            {
                if (p == null || !NetGameplaySyncBridge.IsSessionActive) return;
                var body = p.body;
                if (body == null || body.isKinematic) return; // container / animating pickups don't scatter
                if (p.spawnedIn != null) return;

                Vector3 pos = p.transform.position;
                float now = Time.time;

                for (int i = _recentSpawns.Count - 1; i >= 0; i--)
                    if (now - _recentSpawns[i].Time > SeparationWindow) _recentSpawns.RemoveAt(i);

                Vector3 away = Vector3.zero;
                int crowd = 0;
                foreach (var r in _recentSpawns)
                {
                    Vector3 d = pos - r.Pos; d.y = 0f;
                    if (d.sqrMagnitude <= SeparationRadiusSqr)
                    {
                        crowd++;
                        if (d.sqrMagnitude > 1e-4f) away += d.normalized;
                    }
                }

                _recentSpawns.Add(new RecentSpawn { Pos = pos, Time = now });
                if (crowd == 0) return; // lone drop — leave vanilla motion untouched

                Vector3 dir;
                if (away.sqrMagnitude > 1e-4f)
                {
                    dir = away.normalized;
                }
                else // perfectly stacked — fan successive drops out by the golden angle (deterministic, no RNG divergence)
                {
                    float ang = _separationFan++ * 2.399963f; // ~137.5° in radians
                    dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                }

                float mag = SeparationImpulse * Mathf.Min(crowd, 3);
                body.AddForce(dir * mag + Vector3.up * 0.3f, ForceMode.VelocityChange);
            }
            catch { /* separation is purely cosmetic — never let it break a spawn */ }
        }

        // ----------------------------------------------------------------- registry / lifecycle

        private static void Register(Pickup p, string owner, ushort seq, ItemDefinition item, InventoryData data, bool rotated)
        {
            string key = owner + "#" + seq;
            // A pooled Pickup instance may be reused for a different item — drop any stale binding first.
            UnregisterPickup(p);
            var e = new Entry
            {
                OwnerPeerId = owner, Seq = seq, Key = key, ItemSO = item, Data = data, Rotated = rotated,
                IsLocalOwner = owner == NetGameplaySyncBridge.LocalPeerId,
                SpawnTime = Time.time,
            };
            _byPickup[p] = e;
            _byKey[key] = p;
        }

        /// <summary>Drop a pickup from the registry — called from the RemovePickup prefix (covers pool release / destroy)
        /// and from our own removal path. Idempotent.</summary>
        public static void UnregisterPickup(Pickup p)
        {
            if (p == null) return;
            if (_byPickup.TryGetValue(p, out var e))
            {
                _byPickup.Remove(p);
                if (e != null && _byKey.TryGetValue(e.Key, out var cur) && cur == p)
                    _byKey.Remove(e.Key);
            }
        }

        private static void RemoveWorldPickup(Pickup p)
        {
            try
            {
                var im = StaticInstance<InteractionManager>.Instance;
                if (im == null) return;
                im.RemoveInteractable(p);  // drop from interactable / moving lists
                im.RemovePickup(p);        // pool release / destroy
            }
            catch (Exception ex) { NetLogger.Warn($"[WorldPickup] remove-from-world failed: {ex.Message}"); }
        }

        public static void Clear()
        {
            _byPickup.Clear();
            _byKey.Clear();
            _claimed.Clear();
            _deadKeys.Clear();
            _requestCooldown.Clear();
            _pendingRemovals.Clear();
            _recentSpawns.Clear();
            _separationFan = 0;
            _seq = 0;
        }

        // ----------------------------------------------------------------- helpers

        /// <summary>
        /// Would <c>ItemGrid.AddItem(item, isPickup:true, …)</c> actually accept this item right now? Mirrors the game's
        /// real success predicate — a conservative approximation is unsafe here: <see cref="TryBeginTake"/> blocks the
        /// vanilla pickup for a synced item and, when this returns false, issues NO take request/grant, so the drop is
        /// left in the world with no resolution path. A false negative therefore makes the item permanently
        /// un-collectable on that peer (and on everyone, if both bags are in that state) — issue #11. AddItem succeeds
        /// when the item is auto-consumed, fits in <b>either</b> orientation, or a free equipment slot of its type exists;
        /// checking only the default grid orientation missed rotatable / equippable / consumable drops.
        /// </summary>
        private static bool HasRoomFor(ItemDefinition item)
        {
            try
            {
                if (item == null) return false;
                ItemGrid grid = StaticInstance<UIManager>.Instance != null
                    ? StaticInstance<UIManager>.Instance.PlayerBackpackGrid : null;
                if (grid == null) return false;

                // Auto-consumed items are accepted directly (never need grid/slot space).
                if (item.automaticConsume && grid.IsOwnedByPlayer) return true;

                // Fits in the grid in either orientation (AddItem tries both).
                if (FitsAnyOrientation(grid, item.inventorySize)) return true;

                // Or drops straight into a free equipment slot of its type (weapon / gadget / armor / …).
                var player = StaticInstance<GameManager>.Instance != null
                    ? StaticInstance<GameManager>.Instance.PlayerScript : null;
                EquipmentManager em = player != null ? player.equipmentManager : null;
                if (em != null && em.GetFreeSlotOfType(item.slotType) != InventorySlot.None) return true;

                return false;
            }
            catch { return false; }
        }

        private static bool FitsAnyOrientation(ItemGrid grid, Vector2Int size)
        {
            Vector2Int a = grid.GetPossibleSpace(size, true, true);
            if (a.x >= 0 && a.y >= 0) return true;
            Vector2Int b = grid.GetPossibleSpace(new Vector2Int(size.y, size.x), true, true);
            return b.x >= 0 && b.y >= 0;
        }

        private static NetWorldPickupSpawn BuildSpawnMessage(Pickup p, string owner, ushort seq)
        {
            var msg = new NetWorldPickupSpawn
            {
                OwnerPeerId = owner,
                Seq = seq,
                Position = p.spawnPoint,
                ItemId = p.ItemSO.id.value,
            };

            InventoryData d = p.inventoryData;
            msg.HasData = d != null;
            if (d != null)
            {
                msg.AttachmentIds = ToValues(d.attachmentIds);
                msg.EnchantmentIds = ToValues(d.enchantmentIds);
                msg.CaliberId = (int)d.caliberId;
                msg.CurrentAmmo = d.currentAmmo;
                msg.Quantity = d.quantity;
                msg.Rotated = d.rotated;

                var srcAttrs = d.attributes != null ? d.attributes.itemAttributes : null;
                if (srcAttrs != null && srcAttrs.Length > 0)
                {
                    var ids = new List<ushort>(srcAttrs.Length);
                    var vals = new List<float>(srcAttrs.Length);
                    foreach (var a in srcAttrs)
                    {
                        if (a == null || a.value == null) continue;
                        ids.Add((ushort)a.id);
                        vals.Add(a.value.BaseValue);
                    }
                    msg.AttrIds = ids.ToArray();
                    msg.AttrValues = vals.ToArray();
                }
            }
            return msg;
        }

        private static InventoryData BuildInventoryData(NetWorldPickupSpawn m, ItemDefinition item)
        {
            ItemId[] attachIds = ToItemIds(m.AttachmentIds);
            ItemId[] enchIds = ToItemIds(m.EnchantmentIds);

            int n = m.AttrIds != null ? m.AttrIds.Length : 0;
            var attrData = new ItemAttributeData[n];
            for (int i = 0; i < n; i++)
                attrData[i] = new ItemAttributeData((ItemAttributes)m.AttrIds[i], new CharacterStat(m.AttrValues[i]));
            var coll = new ItemAttributeCollectionData { itemAttributes = attrData };

            int xs = item.inventorySize.x, ys = item.inventorySize.y;
            return new InventoryData(new ItemId(m.ItemId), 0, 0, m.Quantity, m.CurrentAmmo,
                (CaliberTypes)m.CaliberId, coll, attachIds, enchIds, 0, xs, ys, m.Rotated);
        }

        private static ushort[] ToValues(ItemId[] ids)
        {
            if (ids == null || ids.Length == 0) return Array.Empty<ushort>();
            var a = new ushort[ids.Length];
            for (int i = 0; i < ids.Length; i++) a[i] = ids[i].value;
            return a;
        }

        private static ItemId[] ToItemIds(ushort[] values)
        {
            if (values == null || values.Length == 0) return Array.Empty<ItemId>();
            var a = new ItemId[values.Length];
            for (int i = 0; i < values.Length; i++) a[i] = new ItemId(values[i]);
            return a;
        }
    }
}
