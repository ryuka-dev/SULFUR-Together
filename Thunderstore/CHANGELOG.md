# Changelog

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
