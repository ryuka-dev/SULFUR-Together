# Changelog

## 0.2.0 — Preview

First public preview of **SULFUR Together**, a co-op multiplayer mod.

**Working:**
- Host-authoritative networked sessions over LiteNetLib (`PageDown` to link, `PageUp` to leave)
- Synced level generation / seeds and scene transitions
- Remote player proxies and enemy state mirroring
- Boss-encounter authority (Cousin, Witch, Lucia, Emperor flows)
- Downed / revive co-op flow
- Synced destructibles and world item drops
- Minecraft-style no-pause multiplayer

**Known issues:**
- Clients that load a boss level ahead of the host can desync seeds (e.g. Cousin "infinite dialogue").
- Occasional enemy activation / standing-still edge cases.
- Heavy debug logging in some paths.

> ⚠️ Early preview — expect rough edges. Host and all clients **must run the same mod version**.
