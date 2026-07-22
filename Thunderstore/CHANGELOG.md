# Changelog

## 1.2.3 — Desert crypt co-op

The desert crypt — the locked room with the key and the timed trial — now works in co-op from the
door to the reward room. Before this, only the player carrying the key could get in, the two sides
were given different trials, and finishing one could leave someone shut in. This release changes
the network protocol, so everyone in a session must update together.

**Fixed:**
- **You can no longer be shut inside the crypt after finishing it.** Stepping on the crypt's exit
  was mistaken for entering a sealed combat room, so the way out was closed again a few seconds
  later — with nothing that could ever reopen it. Whoever had not already stepped through was
  trapped for the rest of the level. Room exits are no longer treated as arena seals.
- **The crypt key door now opens for everyone.** Only one key exists in a run, and the door opened
  only for the player who used it — everyone else walked into an invisible wall and could never
  enter. The door now opens on every screen, and nobody else needs a key.
- **Both players now get the same trial.** The trial was picked from unsynced randomness, so one
  player could be hunting chickens while the other was killing enemies in the same room. The trial
  is now decided by the level seed, like the rest of the level.
- **Crypt enemies are now run by the host.** Each side used to spawn its own, so the same spot
  could hold a different enemy for each player. When the host killed one, the other player's enemy
  did not die — it just lost its AI and stood there. Crypt enemies are now spawned once by the host
  and mirrored, so a kill lands for everyone.
- **The reward room now opens for everyone.** Completing the trial opened the reward room — which
  holds the way out — only for the host. Everyone now gets it.
- **A failed trial is now shared.** A failure used to punish only the host. Now the whole group
  fails together.
- **The trial progress bar is now visible to everyone.** Only the host could see what the trial was
  or how long was left. The bar now shows on every screen, in your own language.
- **The Protect trial's altars are now visible to everyone.** The altars you have to defend — and
  their highlight — only appeared for the host, leaving everyone else with nothing to protect.
- **The statue at the crypt entrance no longer slides around.** It could be mistaken for an enemy
  and dragged across the room by whatever that enemy was doing.

## 1.2.2 — Desync, desert and status-effect fixes

A round of sync fixes from player reports: repeated maps, the loading-screen desync, invisible
ghosts, and enemies that ignored a status effect. This release changes the network protocol, so
everyone in a session must update together.

**New:**
- **The four-player limit is gone.** Sessions no longer cap the number of players — a fifth player
  used to sit on "joining" forever, on both Steam and Direct IP. There is no artificial limit now;
  the practical ceiling is your host's connection and CPU.

**Fixed:**
- **Levels no longer repeat for a player who joined a host.** After following the host into a level
  once, that player's own later levels were all generated from the host's old seed, so the same map
  kept coming back. Each level now rolls a fresh layout again.
- **Walking ahead while someone is still on the loading screen no longer desyncs the run.** If a
  player was sitting on the "press to continue" screen and someone else took the exit, players,
  map and enemies would drift apart until the next level reset it. That screen is now handled as
  its own state: the waiting player is led along properly, and the abandoned loading step is shut
  down instead of running its ending against the level that replaced it.
- **Ghosts are no longer invisible to other players.** Ghost enemies picked their species from
  unsynced randomness, so two out of three times the host and the other players disagreed about
  what a ghost was — the enemy stayed invisible on one side while still attacking. Ghost species
  now follows the level seed like the rest of the level does.
- **Status effects from a weapon enchantment now actually affect the enemy.** A Petrification
  enchantment used by anyone but the host would show the petrified look and shatter effect while
  the enemy kept walking. Status effects are now applied on the host, for every player, and are
  shown on everyone's screen — including effects the host applied, which used to be invisible to
  the others.
- **Desert sandworms no longer throw players out of the map or freeze the game.** The ambushing
  sandworms in the desert are now run by the host and mirrored properly: they emerge and land the
  same way for everyone, standing on a landing spot no longer launches you across the level, their
  riders are no longer fought over by two machines, and the frame-long freezes around them are
  gone. A non-host player can also trigger a sandworm ambush now.

## 1.2.1 — Endless boss fixes

Fixes for Endless Mode bosses in co-op. Updating is recommended for everyone in a session.

**Fixed:**
- **Endless bosses can now be fought in co-op.** A boss could die almost the instant it appeared:
  a second player's shots were bypassing the boss's entrance invulnerability, so a strong weapon
  could kill it before its fight even started and the stage would just move on. Bosses now stay
  invulnerable through their intro exactly as they do in single-player, so the fight begins properly.
- **Fixed a duplicate and leftover boss health bar in Endless.** A second health bar could appear
  during a boss fight and stay stuck on screen into later stages. Endless bosses now show a single
  health bar that clears correctly when the boss dies.

## 1.2.0 — Endless Mode co-op

Endless Mode can now be played in co-op. Both players share the same arena, waves, and
enemies (the host runs the world), and the host chooses whether each player progresses
independently — keeping their own XP, level, and cards — or shares one progression track
with a group card vote. This release also fixes several multiplayer sync problems.
All players must update to the same version before joining the same session.

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
- **Endless Mode co-op — Shared progression card vote.** In Shared mode both
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
  In Shared mode you also level from **one shared XP pool** — everyone levels together — and the
  rewards a card drops into the world (loot, companions, shops, chests, and service stations) are
  shared too: single objects both players see in the same place, rather than a separate copy on
  each screen. (Shop purchases and service-station stock stay per-player for now.)

**Fixed:**
- **The Cousin boss fight no longer freezes every player after the boss dies.** In co-op the
  boss could lock all other players in place once it was defeated; the fight now ends cleanly
  for everyone.
- **Fixed a level-generation desync when two players reach an exit at the same time.** Taking an
  exit while the host was already changing levels could build the level twice and leave players
  in differently-shaped copies of the same map. The duplicate transition is now suppressed.
- **A player who falls behind during a level change now catches up to the host's level** instead
  of getting stuck or reloading a different version of the level.
- **Steam joins are faster and more reliable.** Joining by Steam ID no longer requires the host
  to press *Invite Friends* first, connects noticeably faster, and fails quickly instead of
  hanging on a bad attempt. — https://github.com/ryuka-dev/SULFUR-Together/issues/7
- **Fixed hosting from the hub a second time failing to bring joiners in.** After hosting,
  closing the room, and hosting again from the hub, joining players could be left behind; the
  host now keeps the level-generation information it needs to pull them to the hub.

**Polish:**
- **Join flow.** A **"Connecting to host…"** notification now appears on every way of joining, the
  "linked" notification no longer fires the moment you press the button, and if a joined player
  hasn't linked to the host a hint shows the key to press instead of silently leaving them on
  their own map.

**Known issues:**
- In Shared Endless Mode, a level-up card can occasionally show the wrong artwork or text on one
  player's screen. The vote and the reward you receive are always correct — only that card's
  appearance is affected. — https://github.com/ryuka-dev/SULFUR-Together/issues/16
- The known issues from previous releases still apply (occasional enemy snapping, boss desyncs
  when a client loads ahead, unsynced blood/particles/sounds, and 4+ players untested).

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
