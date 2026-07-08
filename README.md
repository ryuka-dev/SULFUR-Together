# SULFUR Together

A **co-op multiplayer mod** for [SULFUR](https://store.steampowered.com/app/2124120/SULFUR/), built as a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin.

**Language:** **English** · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

> **Version 1.0 — Public Beta.** The full co-op loop works, but many systems are still being polished and there is a lot left to optimize. Expect bugs, back up your saves, and make sure every player runs the **same version**.

---

SULFUR Together adds host-authoritative networked play on top of the base game: synchronized level generation/seeds, scene transitions, remote player proxies, enemy state mirroring, boss-encounter authority, a downed/revive co-op flow, destructible and world-item sync, and more. Networking runs over [LiteNetLib](https://github.com/RevenantX/LiteNetLib), and you host or join **from inside the game** over **Direct IP** or **Steam** — no config-file editing.

> This is an unofficial fan-made mod. It is **not** affiliated with or endorsed by Perfect Random or the SULFUR developers. SULFUR and its assets are property of their respective owners.

## Public Beta status

This is a **public test build**. The main loop plays from start to finish, but expect rough edges, visual desyncs, heavy debug logging in some paths, and the occasional broken boss or transition. **Back up your saves.** Host and all clients must run the **same mod version** to connect.

## Requirements

- **SULFUR** (Steam)
- **BepInEx 5** for SULFUR — installed automatically as a dependency
- **SULFUR Native UI Lib 0.10.1** (Thunderstore: `ryuka_labs-SULFUR_Native_UI_Lib`) — installed automatically as a dependency. It powers the in-game connect menu, notifications, and 14-language localization. (The mod runs without it via a *soft* dependency, but then loses its in-game UI.)

## Installing (players)

You do **not** need to build anything to play.

**Recommended — [Gale](https://github.com/Kesomannen/gale) (beginner-friendly mod manager):**

1. Download **Gale** from **https://github.com/Kesomannen/gale** (open **Releases** and grab the latest installer).
2. Install it, open it, and choose **SULFUR** as the game to manage.
3. In **Browse mods**, search **SULFUR Together** and click **Install**. Gale pulls in BepInEx and SULFUR Native UI Lib automatically.
4. Press **Launch game (modded)** in Gale. The first launch is slower while BepInEx sets up.
5. Every player must install the **same version**.

**Manual:** install BepInEx 5, download **SULFUR Native UI Lib** and **SULFUR Together** from Thunderstore, and drop `SULFUR Together.dll` + `LiteNetLib.dll` + the `lang/` folder into `BepInEx/plugins/SULFUR Together/` (and the UI Lib into its own plugin folder).

## Connecting

Co-op is hosted/joined from **inside a loaded save** (not the title screen):

1. Load a save, open **Options → SULFUR Together**, and set your **Player name**.
2. **Host:** press **Create game**, then **Invite Friends via Steam** (no port forwarding) or share the **LAN address** shown on the page for Direct IP. Everyone shares one **Connection key**.
3. **Join:** accept a Steam invite / paste the host's **Steam ID** and press **Join via Steam**, or enter the host's **address + port + connection key** and press **Join game**.
4. **Close room** (host) / **Leave** (client) ends the session.

Settings auto-save as you type. Quick keys: **Page Down** links/follows the host, **Page Up** unlinks back to solo. Config for keybinds and diagnostic toggles still lives in `BepInEx/config/com.ryuka.sulfur.together.cfg`, but connection settings are owned by the in-game page (stored in `coop.json`, kept out of external config managers).

## Building from source

Machine-specific paths (your game install + BepInEx folders) live in `LocalPaths.props`, which is **gitignored** and must never be committed.

1. Copy the template and fill in your own paths:

   ```sh
   cp LocalPaths.props.example LocalPaths.props
   ```

   Edit `LocalPaths.props`:

   ```xml
   <SulfurManagedDir>...\SULFUR\Sulfur_Data\Managed</SulfurManagedDir>
   <BepInExCoreDir>...\BepInEx\core</BepInExCoreDir>
   <BepInExPluginDir>...\BepInEx\plugins</BepInExPluginDir>
   <!-- Optional, for the in-game UI: -->
   <NativeUiLibDll>...\plugins\ryuka_labs-SULFUR_Native_UI_Lib\SULFUR Native UI Lib.dll</NativeUiLibDll>
   ```

   - `SulfurManagedDir` — the game's `Managed` folder (`PerfectRandom.Sulfur.Core.dll`, `Assembly-CSharp.dll`, Unity engine assemblies).
   - `BepInExCoreDir` — BepInEx `core` folder (`BepInEx.dll`, `0Harmony.dll`).
   - `BepInExPluginDir` — BepInEx `plugins` folder (the build auto-deploys here).
   - `NativeUiLibDll` — **optional** compile-time reference to SULFUR Native UI Lib. When set, the in-game connect page / toasts are compiled in (behind `#if NATIVE_UI_LIB`); when unset, the mod still builds, just without its UI.

2. Build:

   ```sh
   dotnet build "SULFUR Together.csproj" -c Release
   ```

   Every build copies `SULFUR Together.dll` + `LiteNetLib.dll` + `lang/*.json` into
   `<BepInExPluginDir>\SULFUR Together\` (note the space — that is the folder BepInEx loads). A **Release**
   build also refreshes the `Thunderstore/` package folder with the same files (see `CopyToRelease` in the csproj).

The build errors early with a clear message if `LocalPaths.props` is missing or its paths are unset.

## Project layout

| Path | What's there |
|------|--------------|
| `src/Plugin.cs`, `src/ModInfo.cs` | BepInEx entry point + mod identity/version |
| `src/Config/` | `CoopConfig` (`.cfg` bindings) + `CoopSettingsStore` (`coop.json` co-op settings) |
| `src/Networking/` | Transport, sessions, run-state/scene/seed sync, remote player proxies |
| `src/Networking/Gameplay/` | Enemy/boss/player gameplay sync, arena lockdown, item drops |
| `src/Patches/` | Harmony patches (boss encounters, weapon fire, pause control, level-gen trace, …) |
| `src/UI/` | In-game connect page, toasts, downed-rescue & run-stats overlays, localization boundary |
| `src/ReverseProbe/` | Diagnostic probes used to reverse-engineer game internals |
| `lang/` | Per-language `*.json` string files (14 languages) |
| `Docs/` | Architecture notes, reverse-engineering audits, development plan |
| `Thunderstore/` | Release manifest, changelog, icon, and packaged README |

## Documentation

The [`Docs/`](Docs/) folder holds the architecture and reverse-engineering notes that drive this mod, including [`NetworkingArchitecture.md`](Docs/NetworkingArchitecture.md), [`BossAuthority.md`](Docs/BossAuthority.md), [`Localization.md`](Docs/Localization.md), [`Versioning.md`](Docs/Versioning.md), and [`DevelopmentPlan.md`](Docs/DevelopmentPlan.md).

## Feedback & bug reports

- **[GitHub Issues](https://github.com/ryuka-dev/SULFUR-Together/issues) (recommended).**
- **Nexus Mods** — the mod's Bug Reports section.

Thunderstore has no bug tracker, so please use one of the above. Include both host **and** client `LogOutput.log` files, who hosted, where it happened, who triggered the event, what each player saw, and your other installed mods.

## License

Licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE). The GPL covers the mod's source code. Game assets (including any sprites derived from SULFUR) remain the property of their original owners and are **not** licensed under the GPL.
