namespace SULFURTogether.Networking
{
    public enum NetMessageType : byte
    {
        HandshakeRequest  = 1,
        HandshakeAccepted = 2,
        HandshakeRejected = 3,
        Ping              = 4,
        Pong              = 5,
        Disconnect        = 6,

        // Reserved for later Phase 2.x lobby/session UI work.
        // No gameplay synchronization is implemented.
        SessionSnapshot   = 7,
        PeerJoined        = 8,
        PeerLeft          = 9,

        // Phase 2.3 run/scene metadata only.
        // This never triggers scene loading or gameplay synchronization.
        RunStateUpdate    = 10,

        // Phase 2.5 host-scene request protocol skeleton.
        // These messages only negotiate metadata and responses; clients never auto-load scenes.
        HostSceneRequest  = 11,
        ClientSceneAck    = 12,
        ClientSceneRefused = 13,

        // Phase 3.0 visual-only remote player proxy.
        // Transform metadata is only applied to local proxy GameObjects, never real gameplay Player/Unit objects.
        PlayerTransformVisual = 14,

        // Phase 4.0-B/C host enemy-death event mirror experiment.
        // Default disabled; clients only apply matched deaths when explicitly enabled.
        HostEnemyDeathEvent = 15,

        // Phase 4.1-A host enemy-state snapshot mirror experiment.
        // Default disabled; clients only match/log drift and never move enemies or change AI.
        HostEnemyStateSnapshot = 16,

        // Phase 4.2.0-A client enemy-death claim experiment.
        // Clients may report local NPC deaths to Host; Host verifies against its own local entity before applying.
        ClientEnemyDeathClaim = 17,

        // Phase 4.3.0-A co-op player downed/revive lifecycle.
        // Does not sync inventory or death penalties; it only delays/commits each peer's own original player death.
        PlayerLifeState = 18,

        // Phase 4.4.0-O3-B Host-authoritative world entity roster.
        // Host sends the full classified entity list after each scene stabilization so that
        // Client can bind Host spawn indices to local entities for strict type-safe matching.
        HostWorldRoster = 19,

        // Phase 5.0 Host-Driven Proxy Architecture.
        // Reliable semantic attack phase event — sent by Host whenever a CombatEnemy
        // transitions through a meaningful attack phase (Windup/Active/Recovery/Cancelled).
        // Client applies this to the puppet enemy Animator directly without native method replay.
        HostAttackPhaseEvent = 20,

        // Phase 5.0 Host-Driven Proxy Architecture (P2).
        // Reliable projectile visual spawn event — sent by Host when an enemy fires a shot.
        // Client spawns a visual-only no-damage proxy object. Damage is host-authoritative.
        HostProjectileVisualSpawn = 21,

        // Phase 5.1 Host-authoritative enemy health sync.
        // Reliable damage event — sent by Host whenever a combat NPC takes damage.
        // Clients use this to track puppet health and reduce death-sync drift.
        HostEnemyDamageEvent = 22,

        // Phase 5.1 Host-authoritative NPC health state snapshot.
        // Reliable health correction — sent alongside HostEnemyDamageEvent when host can read health.
        // Clients use this to align puppet health cache with the host's actual HP values.
        HostEnemyHealthState = 23,

        // Phase 5.3-B Client → Host gameplay hit request.
        // Client sends when local player deals damage to a host-bound puppet NPC.
        // Host validates, applies damage to the real NPC, then broadcasts HealthState/DeathEvent.
        // Architecture: "Client reports intent/evidence. Host owns result."
        ClientHitRequest = 24,

        // Phase 5.3-E Host-authoritative semantic level manifest.
        // Host sends its level-generation RESULT summary (seed, rooms, units, specials) so the
        // client can diff its provisional local world, quarantine client-only combat enemies,
        // and bind host enemies to the correct local instances. ReliableOrdered (auto-fragmented).
        HostLevelManifest = 25,

        // Phase 5.3-F Host → Client hit visual event.
        // Sent when the host applies a validated ClientHitRequest; tells the client to play the
        // native white hit flash on the matching puppet. Carries no health (health stays in
        // HostEnemyHealthState). Visual only — never drives damage or death.
        HostHitVisualEvent = 26,

        // Phase 5.3-K Client → Host generation-input pull request.
        // Sent by a Client whose load gate is waiting for the Host's level generation input. The Host
        // replies with a HostSceneRequest carrying the current scene's seed + used sets + graph name.
        ClientHostGenerationInputRequest = 27,

        // Phase 5.4-E Boss Encounter Authority.
        // Client → Host: "I triggered this boss; please start it authoritatively."
        ClientBossStartRequest = 28,
        // Host → Client: authoritative "this boss encounter has started" (carries reserved phase/position fields).
        HostBossEncounterStart = 29,

        // Phase 5.4-E3 Boss dialog commit (Cousin / Lucia and other dialog-gated bosses).
        // Client → Host: "my player chose to end the boss dialog and start the fight."
        ClientBossDialogCommitRequest = 30,
        // Host → Client: authoritative "the boss dialog is committed — finalize local dialog and start once."
        HostBossDialogCommit = 31,

        // Phase 5.4-E3 Boss phase/state (Witch and other phase bosses).
        // Host → Client: authoritative current boss phase + minimal health/add summary.
        HostBossState = 32,

        // Phase 5.4-E4 Boss dynamic spawn manifest (CousinArm / LuciaEye / Witch illusions / ...).
        // Host → Client: a boss-owned sub-entity was spawned at runtime, identified by owning encounter +
        // add type + per-encounter sequence index (the only cross-end-stable key for same-position adds).
        HostBossDynamicSpawn = 33,

        // Phase 5.4-F BossDamageAuthority. Client → Host: "my hit landed on this boss's target role; apply it
        // through your real ReceiveDamage so the boss mechanic advances." Host owns the actual damage + result.
        ClientBossHitRequest = 34,

        // Phase 5.4-F2 BossDamage feedback. Host → Client: "I accepted a hit on this boss target — play the local
        // hit visual." Visual ONLY; the Client never re-applies damage or advances mechanics.
        HostBossHitVisual = 35,

        // Phase 5.4-F4 fixed-point boss discrete event. Host → Client: an authoritative discrete mechanic event
        // (Cousin Submerge / MoveToNewPool(pool position) / Reappear) so the Client mirrors the SAME pool/dig instead
        // of independently choosing. Carries an optional world position (the chosen pool's appear point).
        HostBossDiscreteEvent = 36,

        // Phase 5.4-F5 Lucia eye defeat authority. Client → Host: "one Lucia eye was defeated locally this eye-cycle."
        // The Host consumes one of its OWN living eyes through the real death path so the vanilla EyeDied/RestartPhases
        // runs host-authoritatively. Count/cycle identity only (no per-eye entity mapping).
        ClientLuciaEyeReport = 37,

        // Phase 5.4-F5 Lucia eye defeat authority. Host → Client: authoritative remaining eye count + cycle. When it
        // reaches 0 the Host's vanilla RestartPhases has run; the Client mirrors cycle-complete (clears residual eyes).
        HostLuciaEyeState = 38,

        // Phase 5.4-F6 Lucia terminal death authority. Host → Client: the Host's Lucia really died (OnBossDead fired).
        // The Client runs a safe local death (real Unit death + boss-end presentation; loot/save isolated) and stops
        // sending hits / applying state for this encounter.
        HostLuciaDeath = 39,

        // Phase 5.4-G2 Witch phase revision authority. Host → Client: an authoritative Witch phase transition with a
        // monotonic revision. The Client applies by revision (Witch phases CYCLE, so the enum can go backwards) and
        // never self-advances its own phase.
        HostWitchPhase = 40,

        // Phase 5.4-G5 Witch Phase 2 dome manifest. Host → Client: the authoritative dome layout (real dome index) for
        // a Phase 2 cycle, captured at ShowWitches. The Client mirrors real/illusion per dome.
        HostWitchP2Manifest = 41,

        // Phase 5.4-G5 Witch Phase 2 hit result. Host → Client: a dome's illusion was defeated, or the real witch was
        // hit (hide all illusions). Drives the Client's hide (its local handlers don't fire — hits route to the Host).
        HostWitchP2Result = 42,

        // Phase 5.5-RT1 runtime spawn sync. Host → Client: a runtime (post-level-stabilization) unit spawned (F3 debug
        // / boss add); the Client mirror-spawns it (UnitId→UnitSO) and binds it to the host SpawnIndex.
        HostRuntimeSpawn = 43,

        // Phase 5.6-WS player weapon bullet sync. Any peer → all others: "my local player fired this weapon barrage."
        // Carries the computed projectile template + count/spread/aim. Receivers replay the barrage through the game's
        // real ProjectileSystem with damage stripped (VISUAL ONLY). Damage stays host-authoritative (ClientHitRequest).
        // Topology mirrors PlayerTransformVisual: a Client sends to Host; the Host stamps the PeerId, replays locally,
        // and relays to all other Clients.
        PlayerWeaponFire = 44,

        // Phase 5.6-WS-2 remote held weapon model. Any peer → all others: "my local player is now holding this weapon
        // (WeaponSO id) with these installed attachments (ItemId list)." Receivers rebuild the weapon model (attachments
        // change the model) and attach it to that player's proxy hands. VISUAL ONLY. Same topology as PlayerWeaponFire.
        PlayerHeldWeapon = 45,

        // Phase 5.6-DL-Q2 client transition relay. Client → Host: "I walked into an exit toward this chapter:level;
        // please perform the transition authoritatively so we both go there." The Host validates (same level, not
        // mid-load), invokes its own GoToLevel (host player moves + generates the seed), then the existing finalized
        // broadcast brings the gated client along. Lets the client LEAD progression while the Host still owns generation.
        ClientTransitionRequest = 46,

        // Phase 5.7-BR in-scene destructible (Breakable) sync. Any peer → all others: "my local player just broke this
        // destructible (keyed by its deterministic spawn position)." Receivers find the matching still-alive local
        // Breakable and call Break() so it shatters/loots/cascades the same on every screen. Peer-authoritative effect
        // mirror (the user-chosen model): each peer's own real break is broadcast; receivers mirror the EFFECT, not the
        // bullet. Loot stays per-peer (already the case — loot is not networked). Same topology as PlayerWeaponFire
        // (Client → Host → relay to other Clients; the firing peer never mirrors its own break).
        BreakableBreak = 47,

        // World item-drop sync. A pickup is born through the single chokepoint InteractionManager.SpawnPickup; player
        // drops carry a non-null InventoryData (Pickup.DroppedByPlayer), loot carries null — the mode filter keys on
        // that (Independent = player drops only; Shared = every pickup). Identity is a composite {ownerPeer, seq}
        // assigned by the dropping peer (world positions are NOT deterministic across peers, so position-matching like
        // BreakableBreak won't work).
        //
        // Spawn (Any→All, optimistic + peer-authoritative, same Client→Host→relay shape as PlayerWeaponFire): the
        // dropping peer's real pickup appears instantly and is broadcast; receivers mirror-spawn the same pickup with
        // the same DIY InventoryData (attachments / enchantments / caliber / ammo / durability+experience).
        WorldPickupSpawn = 48,
        // Take (Client→Host): "I want this pickup." The Host grants the first valid requester (first-come-wins).
        WorldPickupTakeRequest = 49,
        // Removed (Host→All): "this pickup was taken." Every peer removes its local instance; the named taker adds the
        // item to its inventory. Gives the future Shared-loot "first picker takes it, it vanishes for everyone" for free.
        WorldPickupRemoved = 50,
        // Phase RM (room-membership substrate): per-end report "my local player entered boss room X" (Client→Host),
        // fired when the local player crosses the boss's room-entry trigger. Drives the host-authoritative in-room set
        // shared by dialog-sync (synced cutscene for in-room players) and the future arena lockdown (AFK exclusion).
        ClientRoomEnter = 51,
        // Host→All: the authoritative in-room player-id set for boss room X (event-driven, sent on membership change).
        HostRoomMembership = 52,

        // Phase LD-1 generic combat-room gate (MetalGate) sync. Any peer → all others: "a gate at this deterministic
        // world position just closed/opened." MetalGate.Close()/Open() is the single chokepoint (PlayerTrigger room-seal,
        // MetalGateTrigger, AllDeadTrigger open, startClosed init, witch car-chase — all route through it). Gates are
        // per-end independent (each end's local PlayerTrigger only closes its own), so without this an AFK / out-of-room
        // player's gate never matches the others. Receivers find the nearest local MetalGate to the key and call the
        // same Close()/Open() (animation/collider/navmesh — whatever that gate does, reproduced identically). Peer-
        // authoritative effect mirror, same Client→Host→relay topology as BreakableBreak. Foundation for the FF14 arena
        // lockdown (LD-2 adds host-authoritative seal of out-of-room players + popup + teleport on top).
        GateState = 53,

        // Phase LD-1b combat-room door sync, GameObject.SetActive variant (Lucia etc.). Some arenas seal via a
        // PlayerTrigger firing GameObject.SetActive(Doors, true) (not a MetalGate). Any peer → all others: "a trigger at
        // this position activated/deactivated these door-named GameObjects." Receivers find the matching local trigger,
        // read its own onTriggerEvents to get the local door references, and SetActive them. Same Client→Host→relay
        // topology as GateState; the firing peer never mirrors its own.
        TriggerDoors = 54,

        // Phase LD-2a arena lockdown membership feed (Client→Host): "my local player crossed an arena seal trigger at
        // this position." The host builds the per-arena in-room set + lockdown timer (first cross = t0) from these; the
        // host's own crossings are reported locally. Foundation for the FF14 force-seal + teleport of out-of-room players.
        ClientArenaEnter = 55,

        // Phase LD-2b/c arena-lockdown command (Host→all clients). The host computes each arena's non-in-room targets and
        // tells those specific ends to Seal (raise the invisible two-way barrier, t0+5 s), Popup (confirm prompt + arm
        // teleport, t0+10 s), or Release (boss death / fight over → force teleport in + drop the barrier). A receiving end
        // acts only when its local peer id is in the target list; the host applies its own ("host") target locally.
        ArenaCommand = 56,

        // EMP-3a Emperor phase-1 worm HEAD stream (Host→clients, high-rate/unreliable). The client runs its worm
        // kinematic (no autonomous ballistic FixedUpdate — that native physics is the client-only ~1fps) and drives
        // the head from this stream, running only the cheap local UpdateWormSections section-follow. Payload: head
        // world pos (x,y,z) + yaw + sequence.
        // (EMP-6d: also carries the tail's absolute currentHealth so the client P1 boss bar tracks the host — the
        //  quarantined worm tail is not reliably covered by the generic enemy-state mirror.)
        HostEmperorWormHead = 57,

        // EMP-3b Emperor worm section-destruction event (Host→clients, ReliableOrdered). Sent from a postfix on the
        // Host's real DestroySection(index) call. The client's local WeakpointHit never fires (damage is fully
        // host-authoritative), so it never derives this itself — it mirrors by invoking the SAME native
        // DestroySection(index)+MoveVulnerableSectionUp() pair on its own worm (index recomputed locally from its
        // own lastActiveIndex, which stays in lockstep because it only ever changes in response to this event).
        // Payload: sequence (diagnostic/log only — order/delivery already guaranteed by the channel).
        HostEmperorWormSectionDestroy = 58,

        // EMP-3c Emperor worm terminal death (Host→clients, ReliableOrdered, one-shot). Sent from a postfix on the
        // Host's DeathAnimation() kickoff (fires the instant WeakpointHit starts the coroutine, not 5s later). The
        // client's local WeakpointHit never reaches lethal health itself, so it never starts its own DeathAnimation
        // — it mirrors by starting the SAME coroutine on its own worm, which natively plays the death sequence and
        // calls EmperorBossFightHelper.StartPhase2() at the end — the phase-1→phase-2 handoff, for free.
        HostEmperorWormDeath = 59,

        // EMP-3d Emperor worm damage authority (Client→Host, ReliableOrdered). The worm's vulnerable tail section is a
        // runtime SpawnUnitAsync add (not in the level-load manifest) and the Emperor is not a registered boss
        // encounter, so the ordinary roster ClientHitRequest path quarantines it as "client-only" → client hits never
        // reach the host (Log250-252: hitRecv=0, section quarantined 16-31×). This dedicated single-target route
        // forwards a client hit on the tail straight to the host, which applies it to its real lastSectionNpc via the
        // vanilla ReceiveDamage (fires onDamageRecieved → WeakpointHit → section-destroy/death). Payload: damage +
        // damageType + seq. Mirrors the §7.5 design that was assumed working but the head-streamed local worm broke.
        ClientEmperorWormHit = 60,

        // EMP-4 Emperor fight-start (dialog) sync. Log254 pinned the Emperor's fight-start: EmperorBossWorm.StartMovement
        // is invoked by the pre-fight dialog's final MultipleChoiceNode option (a NodeCanvas ActionNode →
        // ExecuteFunction_Multiplatform reflection-invoke), fired INDEPENDENTLY per end on each player's own dialog
        // choice → unsynced Initialize (section spawn) / emergence / music. The Emperor is not a registered encounter
        // (its adapter is diagnostic-only), so the ClientBossDialogCommit (30/31) machinery does not apply — this is a
        // dedicated bespoke gate reusing only the primitives. Whoever picks the option commits; the fight starts
        // host-authoritatively on every end at once.
        //
        // Client→Host: "my player picked the Emperor fight-start dialog option; please commit." Carries nothing (the
        // single active worm is implicit).
        ClientEmperorFightStart = 61,
        // Host→Clients: "commit — start the worm now." Every client invokes the real StartMovement on its local worm so
        // Initialize/emergence/music all begin in step with the host. Carries nothing.
        HostEmperorFightStart = 62,

        // ===== EMP-6b Emperor phase-2 SPIDER sync (see Docs/EmperorBossAudit.md §9). The spider is kinematic and walks
        // a fixed waypoint path at a speed proportional to the LOCAL player distance, so the two ends drift in path
        // progress (Log259). Same host-authoritative shape as the worm: stream the body transform, gate the
        // dialog-driven fight-start (Initialize), route damage client→host, and mirror discrete mechanics + death. =====

        // Host→clients (Unreliable, latest-wins): the spider body transform stream — position + yaw + the two waypoint
        // indices (so the client's CheckDeathTriggerReached converges to the same death waypoint) + absolute currentHealth
        // (EMP-6c: the P2 arena has no net-run-state so the generic enemy-state health mirror is inactive) + seq.
        HostEmperorSpiderTransform = 63,

        // EMP-6b fight-start: the spider's Initialize() is invoked by the phase-2 dialog (a NodeCanvas action, same
        // transport as the worm's StartMovement — Log259). Gate it host-authoritatively so all ends stand up together
        // regardless of who does the dialog / who has jumped down into the phase-2 pit yet.
        // Client→Host: "my player triggered the phase-2 dialog; please commit." Carries nothing.
        ClientEmperorSpiderFightStart = 64,
        // Host→Clients: "commit — Initialize the spider now." Every client invokes the real Initialize on its local
        // spider so the startup animation / stand-up / boss UI begin in step. Carries nothing.
        HostEmperorSpiderFightStart = 65,

        // EMP-6b damage authority (client→host, Reliable): the spider npc is a normal roster unit but the client runs
        // its own local boss (not a puppet), so its hits are not forwarded (Log259 clientHitSent=0). Route them like the
        // worm's EMP-3d: damage + damageType + seq → host applies the real ReceiveDamage on its spider npc.
        ClientEmperorSpiderHit = 66,

        // EMP-6b discrete mechanic + death (host→clients, Reliable): eventCode + seq. Codes: 1=Defend (invuln window),
        // 2=RapidFire (rocket launcher), 3=Death (OnNpcDeath walk-to-death begins). Client replays the native method.
        HostEmperorSpiderEvent = 67,

        // RS-1/RS-2: end-of-Run player statistics cards (Host→Clients, ReliableOrdered, one-shot per Run). The Host
        // tracks all 7 per-player stats live throughout the Run (see NetRunStatsManager) and finalizes/broadcasts
        // this the instant its authoritative transition target is the safe hub (NetSceneClassify.IsHubOrSafeZone*).
        // Payload: ordered player list (by session Slot) + a monotonic RunSeq so a client can ignore a stale/duplicate
        // resend. Clients never send their own numbers back for this — see NetPlayerLifeStateKind.DamageTakenReport
        // for the one input the Host cannot compute itself.
        RunStatsFinalized = 68,

        // FF-1 (Client→Host, ReliableOrdered): "my player's shot hit player X for D". Sent only when the session
        // friendly-fire setting is ON and the local classifier positively identified the damage source as the local
        // player (fail-closed). The host stamps the source peer from the authenticated connection (never trusts the
        // wire value), re-validates the FF setting + victim downed state, then applies (victim==host) or relays via
        // the existing PlayerLifeState HostDamageRequest channel (victim==another client).
        PlayerFriendlyFireHit = 69,

        // FF-1 (Host→Clients, ReliableOrdered): host-authoritative session settings snapshot (currently just the
        // friendly-fire flag) + a monotonic revision so a client ignores stale re-sends. Broadcast on toggle change
        // and pushed once to each newly accepted peer right after the handshake.
        SessionSettings = 70,

        // HZ-2 (any peer → Host → relay to other Clients, ReliableOrdered): a thrown Breakable-type throwable (poison/fire
        // flask, explosive, etc.) broke locally on the throwing peer. Carries the throwable's UnitSO id + the locked impact
        // position/rotation so every other peer spawns that unit at the same spot and Break()s it, mirroring the on-break
        // EFFECT (ground hazard / explosion VFX). Throwables are spawned at runtime (non-deterministic), so unlike the
        // in-scene BreakableBreak channel there is no existing local instance to match — the receiver spawns one.
        ThrowableEffect = 71,

        // HZ-3 (any peer → Host → relay, ReliableOrdered): a throwable was released. Carries the weapon ItemId + launch
        // position/velocity so peers render a visual-only flying body (the on-break effect still comes via ThrowableEffect).
        ThrowableFlight = 72,

        // Issue #5 (Client → Host, ReliableUnordered): a client entered a one-shot TriggerSpawner ambush. Carries the
        // trigger's world position (seed-deterministic key) so the host runs the real spawn once (first-trigger-wins);
        // the spawned skeletons then reach every peer via the existing HostRuntimeSpawn mirror.
        ClientTriggerSpawn = 73,

        // External mod payload (public integration surface, see SULFURTogether.Api.NetExternalChannel). Carries an
        // opaque {channelId, payload} that ST relays over the session transport but never interprets. Lets a
        // companion mod run its own host-authoritative protocol without ST learning its message ids. The sender
        // peer id is stamped from the authenticated connection, not read from the wire.
        ExternalModPayload = 74,

        // WID-2 (owner → Host → relay, ReliableOrdered): a synced world pickup came to rest on its owning peer.
        // Carries the pickup key + authoritative settled position so mirror peers snap their local instance there
        // (their independent physics — plus inter-item collisions in co-op's no-pause drops — otherwise diverge).
        // One-shot per drop (re-sent only if the item is later disturbed and re-settles). See WorldPickupManager.
        WorldPickupSettle = 75,

        // VOTE-1: generic host-authoritative session-vote subsystem (first consumer: EnableDevMode, issue #8).
        // Client → Host: request to start a vote of a given kind. The host validates (no current vote, cooldown,
        // in-session) and starts it; the host's own "propose" is a local call, not this message.
        ClientVoteStart = 76,
        // Client → Host: this client's cast (Agree / Decline) for the current vote of a given kind.
        ClientVoteCast = 77,
        // Host → all (ReliableOrdered): the authoritative vote snapshot — kind, rule, phase, outcome, countdown and
        // every participant's choice. Monotonic revision so clients drop reordered re-sends. Host owns the tally and
        // the clock; clients only display and forward casts.
        HostVoteState = 78,

        // DB-1: a level-gen DoorBlocker (the 0.18 inter-chunk hold-to-open door) was opened by a player. Peer →
        // everyone else, keyed by the door's deterministic world position. Opening is a one-way, irreversible
        // transition, so the mirror is idempotent and needs no arbitration. Only doors that start closed are
        // carried here; the trap doors' slam-shut is deliberately NOT synced.
        DoorBlockerOpen = 79,

        // K-1 (issue #10): a projectile-path throwable (ThrowingKnives — ThrowableWeapon.Throw's prefabToThrow==null
        // branch, a real Custom ProjectileSystem projectile, not a Breakable). The throwing peer broadcasts the weapon
        // ItemId + the exact launch ray; receivers replay one damage-stripped Custom projectile with the resolved
        // customVisuals so the knife's flight + stick appear on every screen. Visual only — same topology as
        // PlayerWeaponFire (thrower never replays its own; Client→Host→relay; receivers skip their own PeerId).
        ThrowableProjectile = 80,

        // SL-2 (Shared-loot): chest (Container) open sync, host-authoritative (Model A). A client that interacts asks the
        // host to open the chest (ChestOpenRequest, client→host, keyed by the container's deterministic world position);
        // the host opens its matching chest (rolls loot + SpawnPickup, mirrored via the shared-loot world-drop channel)
        // and broadcasts ChestOpened (host→all) so every peer plays the open animation + marks it looted (visual only).
        ChestOpenRequest = 81,
        ChestOpened      = 82,

        // SL-2b (Shared-loot): LootableObject sync (food/material/scavenge hatboxes + cash register — a non-Container
        // loot class triggered via Interactable → LootableObject.Trigger). Same host-authoritative request/broadcast
        // shape + wire DTO (NetChestOpen) as the chest channel: a client asks the host to trigger its matching object
        // (LootableTriggerRequest, client→host, position-keyed) and the host broadcasts the trigger so every peer
        // replays the animation (LootableTriggered, host→all); the items themselves mirror via the world-drop channel.
        LootableTriggerRequest = 83,
        LootableTriggered      = 84,
    }
}
