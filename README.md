# SULFUR Together

A work-in-progress **co-op multiplayer mod** for the game [SULFUR](https://store.steampowered.com/app/2124120/SULFUR/), built as a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin.

It adds host-authoritative networked play on top of the base game: synchronized level generation/seeds, scene transitions, remote player proxies, enemy state mirroring, boss-encounter authority, downed/revive co-op flow, and more. Networking runs over [LiteNetLib](https://github.com/RevenantX/LiteNetLib).

> ⚠️ **Early/experimental.** This is a private development build that hooks deeply into the game's internals via Harmony. Expect rough edges, debug logging, and breaking changes between versions.

> This is an unofficial fan-made mod. It is **not** affiliated with or endorsed by Perfect Random or the SULFUR developers. SULFUR and its assets are property of their respective owners.

## Requirements

- **SULFUR** (Steam)
- **BepInEx 5** installed for SULFUR — these instructions assume it is managed via [Gale](https://github.com/Kesomannen/GaleModManager)
- **.NET SDK** (targets `net472`) and an MSBuild toolchain (Visual Studio 2022 or `dotnet` CLI)

## Building

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
   ```

   - `SulfurManagedDir` — the game's `Managed` folder (provides `PerfectRandom.Sulfur.Core.dll`, `Assembly-CSharp.dll`, and the Unity engine assemblies referenced by the build).
   - `BepInExCoreDir` — BepInEx `core` folder (provides `BepInEx.dll` and `0Harmony.dll`).
   - `BepInExPluginDir` — BepInEx `plugins` folder (the **Release** build auto-deploys here).

2. Build:

   ```sh
   dotnet build "SULFUR Together.csproj" -c Release
   ```

   A **Release** build copies `SULFUR Together.dll` + `LiteNetLib.dll` into
   `<BepInExPluginDir>\SULFURTogether\` automatically. A **Debug** build just compiles to `bin/`.

The build will error early with a clear message if `LocalPaths.props` is missing or its paths are unset.

## Usage

Configuration lives in the standard BepInEx config file
(`BepInEx/config/com.ryuka.sulfur.together.cfg`), generated on first launch.

Key settings:

- `NetworkMode` — `Off` / `Host` / `Client`
- `HostAddress`, `HostPort`, `PlayerName` — connection settings (user-owned)
- A large set of experimental gameplay toggles (enemy sync, boss authority, revive, etc.)

At runtime: `PageDown` to link up (multiplayer), `PageUp` to leave. See the in-game
config for the full list of keys and toggles.

## Project layout

| Path | What's there |
|------|--------------|
| `src/Plugin.cs`, `src/ModInfo.cs` | BepInEx entry point + mod identity |
| `src/Config/` | `CoopConfig` — all BepInEx config bindings |
| `src/Networking/` | Transport, sessions, run-state/scene/seed sync, remote player proxies |
| `src/Networking/Gameplay/` | Enemy/boss/player gameplay sync |
| `src/Patches/` | Harmony patches (boss encounters, weapon fire, pause control, level-gen trace, …) |
| `src/ReverseProbe/` | Diagnostic probes used to reverse-engineer game internals |
| `src/Logging/` | Gated logger |
| `Docs/` | Architecture notes, reverse-engineering audits, development plan |
| `Art/`, `src/Resources/` | Sprite assets used by remote player visuals |

## Documentation

The [`Docs/`](Docs/) folder contains the architecture and reverse-engineering notes that
drive this mod, including [`NetworkingArchitecture.md`](Docs/NetworkingArchitecture.md),
[`BossAuthority.md`](Docs/BossAuthority.md), and [`DevelopmentPlan.md`](Docs/DevelopmentPlan.md).

## License

Licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

The GPL covers the mod's source code. Game assets (including any sprites derived from
SULFUR) remain the property of their original owners and are **not** licensed under the GPL.
