# SULFUR Together

**SULFUR Together** is an experimental co-op multiplayer mod for **SULFUR**.

Explore the same procedurally generated levels, fight enemies together, revive downed players, see each other's weapons and attacks, break objects in the world, and drop items for other players to pick up.

## Pre-Alpha Warning

> **This is a highly experimental Pre-Alpha preview.**
>
> The mod currently contains many bugs, incomplete systems, visual inconsistencies, synchronization problems, and possible progression-blocking issues.
>
> Normal gameplay is **not guaranteed**. Crashes, softlocks, desyncs, broken boss encounters, and compatibility-breaking updates may occur.
>
> Back up your save files before playing.
>
> Every player must use the same version of SULFUR and the same version of SULFUR Together.

This release is intended for players who are willing to test unfinished multiplayer functionality and report problems.

## Current Features

* Host-authoritative multiplayer networking
* Synchronized procedural level generation
* Synchronized level transitions
* Remote player movement and visual representation
* Remote held weapons
* Player projectile synchronization
* Basic enemy movement and combat synchronization
* Host-authoritative enemy damage and deaths
* Enemy ranged attack and projectile synchronization
* Co-op downed, revive, and death flow
* Basic synchronization for several boss encounters
* Synchronized destructible world objects
* Player-dropped item synchronization
* Independent player characters, inventories, equipment, progression, and personal loot
* Client-initiated level transitions routed through the host
* Multiplayer sessions remain active when returning to the hub

Some visual effects, animations, blood effects, sounds, and secondary combat details may still appear differently between players.

## Player Count

The current configuration limits a session to **4 players**, including the host.

The networking architecture does not currently have a known hard limit, but sessions with **4 or more players have not been properly tested**. Actual performance and stability will depend heavily on the host's computer, network conditions, and the number of active enemies.

For the most predictable experience, testing with two players is currently recommended.

## Saves, Inventory, and Loot

SULFUR Together does not use a fully shared character save.

Each player keeps their own:

* Character
* Inventory
* Equipment
* Progression
* Personal loot
* Save data

Items intentionally dropped into the world by a player can be synchronized so another player can pick them up.

A host room-management interface is planned for future versions. It is intended to provide room settings and gameplay options, including control over loot behavior.

That interface has **not been implemented yet**. The current Pre-Alpha uses independent personal loot.

## Boss Support

Boss synchronization remains incomplete.

The most basic synchronization paths have currently been implemented and tested for:

* Witch
* Lucia
* Cousin

Even these encounters may still contain visual, timing, phase, dialogue, or progression problems.

Other bosses have varying amounts of incomplete synchronization and may contain issues that directly affect gameplay or prevent the encounter from progressing correctly.

Do not assume that every boss can currently be completed reliably in multiplayer.

## Connecting to a Session

SULFUR Together currently does not have an in-game lobby or connection interface. Network settings must be configured through:

```text
BepInEx/config/com.ryuka.sulfur.together.cfg
```

### Host settings

The host should configure:

```ini
[Network]

EnableNetworking = true
NetworkMode = Host
HostPort = 9050
PlayerName = Host
ConnectionKey = ChooseYourOwnPrivateKey
```

The host must allow the configured **UDP port** through the local firewall.

### Client settings

The client should configure:

```ini
[Network]

EnableNetworking = true
NetworkMode = Client
HostAddress = HOST_IP_ADDRESS
HostPort = 9050
PlayerName = Player
ConnectionKey = ChooseYourOwnPrivateKey
```

`ConnectionKey` must be identical for the host and every client.

### Joining and leaving multiplayer

After connecting:

* Press **Page Down** on the client to enter the linked multiplayer state and follow the host.
* Press **Page Up** on the client to leave the linked multiplayer state and return to independent local play.
* The host can use **Page Down** to toggle the multiplayer-linked state.

A client connecting to the host does not always mean that the client's current single-player run will immediately be replaced. The explicit link action exists to avoid unexpectedly taking control of an unfinished local run.

## Playing Over the Internet

The mod communicates directly with the host over UDP.

Players on the same local network may be able to connect using the host's local IP address.

For players on different networks, possible approaches include:

* Manually forwarding the selected UDP port on the host's router
* Using a virtual LAN service
* Using another private networking solution that allows direct communication between the players

A virtual LAN generally works by having every player join the same private virtual network and then entering the host's virtual-network IP address as `HostAddress`.

SULFUR Together is not affiliated with, responsible for, or able to provide support for third-party VPN, tunneling, or virtual LAN software. Availability, reliability, privacy, regional accessibility, and configuration requirements depend on the service chosen by the players.

## Multiplayer Pausing

The game world does **not pause** while multiplayer is active.

This includes situations such as:

* Opening the inventory
* Opening the pause menu
* Opening development or configuration interfaces
* Entering some dialogue sequences
* Moving the game window out of focus

This behavior is intentional. Allowing one game instance to pause independently can desynchronize enemy behavior, boss timelines, physics, and world events.

Make sure your character is in a safe location before opening menus.

## Known Issues

* Clients that load a boss level ahead of the host can desync, such as the Cousin encounter entering an infinite dialogue loop.
* Occasional enemy activation and standing-still edge cases remain.
* Some enemies may briefly snap, teleport, animate incorrectly, or appear in slightly different positions.
* Enemy visual attributes or behavior variants may occasionally differ between players.
* Some enemy death-spawn and minion-spawn combinations may still have loot or visual-effect inconsistencies.
* Boss dialogue, phases, invulnerability, summoned entities, and terminal events may desynchronize.
* Boss encounters other than Witch, Lucia, and Cousin may have serious gameplay-affecting synchronization problems.
* Blood, damage effects, particles, sounds, and animations are not always synchronized.
* Player visual sprites and weapon positioning are still unfinished.
* Remote players may appear to slide, turn incorrectly, or use the wrong directional sprite.
* Player collision is experimental.
* Level transitions can fail if the host and client trigger incompatible events at nearly the same time.
* Disconnecting or changing network settings during a run may leave temporary invalid multiplayer state.
* Compatibility with other gameplay-changing mods is not guaranteed.
* Multiplayer with four or more players has not been properly tested.
* There is currently no lobby, room browser, connection UI, player-management UI, or host room-settings interface.

This list is not exhaustive.

## Reporting Bugs

Bug reports can be submitted through:

* The **Bug Reports** section on the SULFUR Together Nexus Mods page
* [GitHub Issues](https://github.com/ryuka-dev/SULFUR-Together/issues)

Please include as much of the following information as possible:

* The SULFUR Together version used by every player
* The SULFUR game version
* Both the host and client `LogOutput.log` files
* Which player was the host
* The level, environment, or boss where the problem occurred
* Which player triggered the transition, dialogue, attack, pickup, or other relevant event
* A description of what each player saw
* Whether the problem can be reproduced
* A list of other installed mods
* Screenshots or a video, when available

Reports containing both the host and client logs are significantly more useful than a report containing only one side.

Please do not send only a description such as “multiplayer did not work.” The order of events on both machines is often necessary to identify a synchronization problem.

## Source Code and Development

SULFUR Together is open source.

GitHub repository:

https://github.com/ryuka-dev/SULFUR-Together

The project is still under active development. Systems may be redesigned, configuration options may change, and new versions may not be compatible with older releases.

Community testing and detailed bug reports are appreciated.

## Disclaimer

This is an unofficial fan-made mod. It is **not** affiliated with or endorsed by Perfect Random or the SULFUR developers. SULFUR and its assets are property of their respective owners.
