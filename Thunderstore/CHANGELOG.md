# Changelog

## Unreleased

Work in progress toward the next release. All players must update to the same version
before joining the same session.

**New:**
- **Endless Mode co-op — Independent progression.** Endless Mode now stays in sync between
  players: everyone shares the same arena, waves, and enemies (the host runs the world), while
  each player keeps their own XP, level, and card picks. Experience is awarded to whoever killed
  an enemy — the reward orb flies straight to the killer, even on a long-range shot — and the host
  can choose whether the kill credit goes to the player who dealt the *first* hit or the *last* hit
  (default: last hit). Leveling up no longer freezes the game for the other player: the world keeps
  running while you pick your card, and you stand safely still (invulnerable, and enemies stop
  targeting you) until you choose. Enemies also pick their target by distance now, so they no longer
  all pile onto the host.
- **Endless Mode co-op — Shared progression card vote (in development).** In Shared mode both
  players now see the same floating cards on level-up and **vote together** on which one to take:
  aim at a card and fire to cast your vote, and your name is stamped onto the card you picked (your
  own stamp in gold, teammates' in red). The most-voted card wins — ties are broken by the host — and
  the chosen card is applied for everyone. There's no timer until someone casts the first vote, so
  you can take your time discussing. You can also vote to **Skip** or **Reroll** — a Skip gives each
  player their pass reward, and a winning Reroll deals a fresh set of cards for everyone to vote on
  again (the reroll card is unavailable once your shared rerolls run out). You can also **vote to
  banish** a card you don't want — aim at its dismiss button and fire; it counts as your one vote,
  competing with everyone's picks. If a banish wins, that card is removed for everyone (spending one
  shared banish) and you vote again on the rest. You can **change or cancel** your vote at any time
  by re-selecting, and the countdown only runs once at least one vote has been cast.
  *(One shared XP pool and shared world-card rewards are still in development.)*

**Fixed:**
- **Endless Mode card loot no longer appears twice.** In Shared mode, a level-up card that
  drops loot into the world now spawns it once, at the same place for both players, instead of
  a duplicate copy on each screen.
- **Endless Mode companions are shared.** A card that summons an ally now spawns a single
  companion that both players see in the same place (it follows the host), with the charmed
  heart above its head, instead of a separate copy on each screen.
- **The yellow "loot spawned here" beam is now visible to everyone.** In Shared mode the pillar
  that marks where a card dropped loot, a chest, or an NPC now appears, moves, and disappears in
  sync for both players.
- **Endless Mode shop NPCs are shared.** A card that spawns a vendor now places a single shop that
  both players can walk up to and buy from, instead of a separate one on each screen. (Purchases
  are per-player for now.)
- **Endless Mode chests and stations from cards are shared.** In Shared mode, a card that spawns a
  chest, storage stash, or service station now creates a single object at the same place for both
  players — you both see it, at the same spot, and can use it — instead of a separate copy on each
  screen at a different position (which the other player couldn't open). With shared loot on, opening
  a card chest gives one shared reward, as it does for world chests. (Service-station stock is still
  per-player for now.)
- **Endless Mode card effects work correctly for non-host players.** The Infinite Ammo and
  Indestructible cards now wear off after their duration for the client too (they used to last the
  whole run), and the "more XP" card now actually speeds up the shared XP bar.
- **Endless Mode enemy corpses no longer pile up for non-host players.** Defeated enemies now sink
  into the ground and clear away on every player's screen, matching the host, so a long run no longer
  fills the arena with corpses and slows the client down.
- **Endless Mode companions work for non-host players in Independent mode.** When a non-host player
  picks a "summon ally" card, the companion is now spawned as a real fighting ally that follows that
  player and can actually damage enemies, instead of a local copy that couldn't hurt anything and was
  invisible to the host.

## 1.1.1 — Co-op fixes and polish

A small update with multiplayer fixes and quality-of-life improvements.
All players must update to the same version before joining the same session.

**Fixed:**
- **Players now stay together in non-standard levels.** When the host loaded a level
  that isn't one of the normal story chapters — such as a developer/debug level opened
  from the dev menu — other players could be left behind on a different map. The
  follow-the-host logic now covers these levels too.
- **Reviving a downed teammate is more forgiving.** The *Hold [E]* prompt now appears as
  soon as you are in range (from a slightly larger distance), and briefly releasing the
  key — or tapping it — no longer resets the progress to zero, so the revive fills up
  reliably.

**New:**
- **Shared damage numbers on the practice target dummy.** Damage dealt to the ChurchHub
  target dummy now shows its flying numbers and running total to every player, including
  hits dealt by other players.

**Polish:**
- Dropped items now glide smoothly into their resting spot for other players instead of
  snapping into place.

## 1.1.0 — SULFUR 0.18.3 support, shared loot, and fixes

This update makes SULFUR Together compatible with **SULFUR 0.18.3 (Qiosk's Plenty)**,
adds optional **shared world loot**, and fixes several multiplayer problems.
All players must update to the same version before joining the same session.

**New:**
- **Shared world loot (optional, chosen by the host).** Turn on *Shared loot* on the
  connect page and enemy, crate, chest, and hatbox/register loot is shared — the host rolls
  it and the first player to grab each item takes it. Off by default (each player keeps
  their own loot, as before).
- **Developer mode is now host-gated.** It is a session setting that only turns on when
  every player has dev access or a session vote passes, so a single player can no longer
  force it on for everyone. — https://github.com/ryuka-dev/SULFUR-Together/issues/8
- **Inter-chunk doors are synced.** The hold-to-open doors added in SULFUR 0.18 now open
  for everyone when one player opens them.
- **Thrown knives are visible to other players** — you can see the knife fly and stick. —
  https://github.com/ryuka-dev/SULFUR-Together/issues/10
- **External mod channel.** A public API so other mods can send data over the co-op session
  and read basic session info.

**Fixed:**
- **Updated for SULFUR 0.18.3 (Qiosk's Plenty).** Re-bound the damage/hit chain, fixed a
  host-side enemy-AI freeze, and reconnected hooks the update renamed — combat, enemy
  deaths, and the client→host hit path work again on the new game version.
- **Dropped items could become permanently un-collectable for everyone** — fixed. —
  https://github.com/ryuka-dev/SULFUR-Together/issues/11
- **Dropped items now rest in the same place for everyone** and no longer pile into an
  indistinguishable tower.

**Known issues:**
- With shared loot on, the food/material hatbox and cash-register *open animations* do not
  replay on other players' screens (the loot itself is correct and shared). —
  https://github.com/ryuka-dev/SULFUR-Together/issues/14
- The 1.0.0 beta known issues below still apply (occasional enemy snapping, boss desyncs
  when a client loads ahead, unsynced blood/particles/sounds, 4+ players untested).

## 1.0.1 - Bugfix update

This update fixes several multiplayer sync problems found in the first public beta.
All players must update to the same version before joining the same session.

**Fixed:**
- Fixed client poison/fire ground hazards causing excessive duplicate damage.
- Synced throwable landing effects such as poison, fire, and explosions across peers.
- Synced in-flight throwable visuals so other players can see where grenades and flasks are thrown.
- Fixed client weapon XP not being credited on kills.
- Fixed one-shot trigger spawns creating separate local encounters for each player.
- Fixed goblin civilians briefly playing spearman attack animations on clients.

## 1.0.0 — Version 1.0, Public Beta

First public **Beta** of **SULFUR Together**, a co-op multiplayer mod for SULFUR.

This is a public test build. The core loop works end-to-end, but many systems are
still being polished — expect rough edges. Every player must run the **same version**
of SULFUR and of SULFUR Together.

**New since the early preview:**
- **In-game connect menu** — set up and join sessions from Options → *SULFUR Together*.
  No more editing config files.
- **Steam connectivity** — invite friends / join by Steam ID, no port forwarding needed
  (works alongside Direct IP).
- **Direct IP** — host shows its LAN address; clients join with address + port + key.
- **14-language localization** for all on-screen text (via SULFUR Native UI Lib).
- **Friendly fire** as a host-authoritative session setting.
- **End-of-run stats cards** on the return-to-hub screen.
- **Join / leave toasts** and a live player list.

**Working:**
- Host-authoritative networked sessions over LiteNetLib.
- Synced level generation / seeds and scene transitions.
- Remote player proxies, held weapons, and projectiles.
- Enemy state mirroring; host-authoritative enemy damage, deaths, and ranged attacks.
- Downed / revive / death co-op flow with an on-screen rescue prompt.
- Boss-encounter authority for several bosses (Cousin, Witch, Lucia, Desert boss, …).
- Synced destructibles and world item drops.
- Independent per-player character, inventory, equipment, progression, and save.
- No-pause multiplayer (the world keeps running while menus are open).

**Known issues:**
- Clients that load a boss level ahead of the host can desync (e.g. Cousin "infinite dialogue").
- Some enemies may briefly snap, teleport, or animate incorrectly.
- Bosses beyond the tested set may have gameplay-affecting sync problems.
- Blood, particles, sounds, and some animations are not always synchronized.
- Sessions with 4+ players are untested; 2 players is the most predictable.

**Dependencies:** BepInEx 5, SULFUR Native UI Lib 0.10.1 (both installed automatically
through Thunderstore / Gale).

> ⚠️ Public Beta — expect bugs. Back up your saves. Report issues on GitHub (not
> Thunderstore, which has no bug tracker). Host and all clients **must run the same mod version**.
