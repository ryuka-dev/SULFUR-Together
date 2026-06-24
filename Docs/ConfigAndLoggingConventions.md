# Config & Logging Conventions

How settings and diagnostic logging are organised — and the target shape for a public release.

## Core principle: functional ≠ diagnostic

Keep two things strictly separate:

1. **Functional behaviour** — runs unconditionally. It is *not* a config option the player can turn
   off (e.g. client puppet-combat blocking, attack-animation network sync, damage forwarding). The
   Harmony prefix always does its job; only its *logging* is optional.
2. **Diagnostic logging** — gated behind a `Log*` config flag, **default OFF**. Writing log text is
   pure observability; it must never be required for correctness.

Anti-pattern (fixed in 5.7-B3b): the `[Npc] TriggerShoot/SetShooting/SetAimTarget` lines were
*ungated* `Log.Info` calls inside functional prefixes. With many enemies active (Plan B wakes large
groups), synchronous file I/O + reflection in those logs hitched the host frame → client stutter.
Every high-frequency log line must sit behind a `Log*` flag.

`Enable*` vs `Log*`: `Enable*` gates *behaviour* (keep on as needed); `Log*` gates only *logging*
(default off). Never fold the two into one flag.

## Two audiences, two homes

| Audience | What | Where | Default |
|---|---|---|---|
| Players | A few curated **gameplay options** | In-game UI | sensible per-option |
| You (debugging) | Many **debug log switches** | Config file | **all OFF** |

Bug-report flow for end users: *enable the relevant debug switch → reproduce → send the log.* Users
never read logs by default and the game never floods them.

## Target shape for the public release

- Group every diagnostic log switch under a single **`[Debug]`** config section.
- Add one master **"verbose logging"** toggle, **default OFF**. Standard mod-scene pattern:
  user reports a bug → flips the master toggle (or the specific one you name) → reproduces → sends
  the log file.
- Bind defaults of all `Log*` flags = **false** (so a fresh / public install is quiet). The dev
  build can flip any of them on in the config file when needed.
- **Do not force `Log*` values in `ApplyUnpublishedDevelopmentDefaults`** — forcing them there would
  stomp the config value on every load, defeating the "flip the cfg to debug" workflow. Force only
  *functional* dev defaults there; let `Log*` flags ride their (false) bind defaults so the cfg value
  is respected. (This is exactly what 5.7-B3b changed.)
- Curated gameplay options move to the in-game UI; the config file stays a debug surface.

## BepInEx cfg note

The `## Default value: <x>` line BepInEx writes above each key is the **bind default** (the 3rd arg
of `cfg.Bind`), not the live value. The effective setting is the `Key = <value>` line below it.
After 5.7-B3b the bind defaults of the noisy `Log*` flags are `false`, so the comment and the value
agree.
