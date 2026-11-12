# Session requirements

How SULFUR Together handles two players whose installed content differs, and how a mod takes part.
Phases SR-1 to SR-4 in [Versioning.md](Versioning.md); protocol 45.

## The problem

A content mod adds items. Each game process gives those items numbers of its own, and the co-op messages carry
item numbers. Two players with different content can hold the same number for two different items, so a drop,
a held weapon or a thrown flask of one player would show up as something else for the other.

SULFUR Together does not know any content mod. It lets a mod declare what it needs to be equal on both ends,
compares the declarations when a player joins, and keeps the affected numbers off the wire between two players
who differ. The result of a difference is that an item is missing on the other screen. It is never shown as a
different item.

## For a mod: registering

```csharp
using SULFURTogether.Api;

NetSessionRequirements.Register(
    "ryuka.sulfur.contentlib",                 // id: the mod's own, normally its plugin GUID
    SessionRequirementTier.Content,
    "SULFUR Content Lib",                      // shown to players
    () => ComputeDigest(),                     // the state both ends must share
    SessionIdClaim.Items(32768, 39999));       // the item ids the content occupies
```

- Call it once, from the plugin's `Awake`, on the main thread. Declare
  `[BepInDependency("com.ryuka.sulfur.together", BepInDependency.DependencyFlags.SoftDependency)]` and keep the
  call in a class that is only loaded when SULFUR Together is installed. It returns `false` with the reason in the
  log when it is refused, and never throws. Registration is refused while a session runs.
- **id**: 1 to 64 characters of `[A-Za-z0-9._-]`. Two ends compare the entries with the same id.
- **displayName**: at most 48 characters, shown as plain text. Rich-text brackets, backslashes and braces are
  replaced; invisible characters and characters outside the Basic Multilingual Plane are dropped.
- **digest**: 1 to 64 printable ASCII characters without spaces. It is asked for at every handshake, not at
  registration, so content that is only known once the game's databases have loaded is fine. It should cover
  everything that decides which id means which content; for a content library, its table of keys and numbers.
  A callback that throws or returns anything else matches no peer: throw on purpose when the content is in a
  state that must not be trusted.
- **claims**: up to 8 inclusive ranges, all above the game's own item ids (1090 on game 1.0.6). Ids a mod puts on
  the wire outside its claims are not protected by any of this.
- `NetSessionRequirements.ApiVersion` is a property; read it at run time. New parameters only ever arrive as new
  overloads.

Two entries match when both digests exist and are equal and their claims are equal. An entry only one end has
does not match.

## What players see

**Joining a host whose content differs.** The connect page turns into a question: which entries differ (only
you have it, only the host has it, not the same on both sides), what joining costs, and two buttons, Cancel and
Join anyway. Nothing joins unless Join anyway is pressed. Closing the page, a lost connection or leaving the
question unanswered for three minutes all cancel. A decision is remembered for the rest of the game process for
that host's room and those exact differences, so a reconnect does not ask again. When no connect page is open
(a Steam invite accepted in the world, an automatic reconnect to a host that changed) the join is given up and a
message says to join from the page.

**Hosting.** A host never turns a player away for content. It gets a message naming the player and the entries,
and that player's link withholds the affected ids.

**In the session.** Between two players who differ, for the ids inside the claims of the differing entries:

| What | Result for the other player |
|---|---|
| A dropped item | No pickup appears. It stays an ordinary local pickup for the player who dropped it |
| An attachment or enchantment on a dropped weapon | The weapon appears without it; a player who takes it gets it without it |
| The held weapon | Empty hands; an attachment is left off the model |
| A weapon's shot and handling sounds | Not played |
| A thrown item (flight, impact, thrown blade) | Not shown |
| An Endless card chest holding such an item | The chest is not mirrored |

Everything else, including every vanilla item, is exchanged as before. With three or more players the host
relays: an item crosses from one client to another only when it crosses both of their links to the host.

## How it works

- `Session.SessionRequirementRegistry` holds what was registered on this machine.
- The handshake carries each end's requirement block (`Session.SessionRequirementBlockCodec`): the client's at
  the end of `HandshakeRequest`, the host's with its room token at the end of `HandshakeAccepted`. Input from a
  peer is bounded and decoded all or nothing; the host decodes a block only after the connection key and the
  version checks have passed.
- `Session.SessionContentComparison` compares the two lists and yields the differences and the item ranges to
  withhold: the claims of every differing entry, from both ends, cut to ids above the game's own.
- `Session.NetItemIdSeam` holds one policy per peer link and `Session.ItemIdMessageFilter` applies it to every
  message that carries an item id, on what is applied, sent and relayed. A peer that has not completed the
  handshake is unknown and only the game's own ids cross. Replacing the withholding policy with one that
  translates ids between two peers' numbering is a change of policy only; the message sites stay as they are.
- `Session.NetContentConsent` owns the joining player's decision; `UI.CoopConnectPage` shows it.

Item names on the wire (the Endless card state) are not part of this: a name is the same on every machine.
Enemy ids are not covered.
