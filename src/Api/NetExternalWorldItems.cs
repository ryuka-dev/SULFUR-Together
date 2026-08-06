using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace SULFURTogether.Api
{
    /// <summary>
    /// Public, mod-neutral view of which world pickups the session owns, and a host-side way to consume one.
    ///
    /// A mod that destroys things lying on the floor — a spell that burns them, a ritual that eats them, a machine that
    /// swallows them — has to know which of them it is allowed to destroy. ST mirrors some pickups and not others: with
    /// shared loot every pickup is one object seen from several machines, and without it only the ones a player threw
    /// are. Removing a mirrored pickup locally leaves it standing on every other screen, still takeable, with ST's own
    /// registry pointing at an object that is gone here.
    ///
    /// The split this API draws is therefore not "am I the host" and not "is shared loot on" — a calling mod should read
    /// neither. It is one question about one object:
    ///
    /// <list type="bullet">
    /// <item><description><b>Not shared</b> — it exists on this machine alone. Destroy it however you like; nobody else
    /// has an opinion, and nothing needs to be sent.</description></item>
    /// <item><description><b>Shared</b> — call <see cref="Consume"/>. On the host it is removed everywhere and received
    /// by nobody. On a client it returns false, and the correct response is to leave the pickup alone: the host is
    /// running the same effect and will retire it.</description></item>
    /// </list>
    ///
    /// Both configurations then behave the way they should with no branch in the calling mod: what the session mirrors
    /// is retired by the host for the whole room, and what it does not belongs to whichever machine can see it.
    ///
    /// Consuming is not taking. The item goes nowhere — no inventory receives it — which is what distinguishes this
    /// from the pickup path and why it needs an entry point of its own.
    ///
    /// Threading: call from the Unity main thread, as with every other part of this API.
    /// </summary>
    public static class NetExternalWorldItems
    {
        /// <summary>Bumped on any breaking change to this API. A companion mod may gate on it.</summary>
        public const int ApiVersion = 1;

        /// <summary>
        /// Whether the session mirrors this pickup onto other machines, making its removal the host's to declare.
        /// False when there is no session, when drop sync is off, and for any pickup ST is not tracking.
        /// </summary>
        /// <param name="pickup">The object carrying the game's <c>Pickup</c>, or the component itself.</param>
        public static bool IsShared(Component pickup)
            => SULFURTogether.Networking.Gameplay.WorldPickupManager.IsTracked(Resolve(pickup));

        /// <summary>
        /// Host: remove a shared pickup from the world on every peer, as consumed rather than as picked up.
        /// </summary>
        /// <returns>
        /// True when this machine removed it and told everyone. False when the pickup is not shared, when this peer is
        /// not the host, or when it had already been granted to someone taking it — in every one of those cases the
        /// caller must <b>not</b> remove it itself.
        /// </returns>
        public static bool Consume(Component pickup)
            => SULFURTogether.Networking.Gameplay.WorldPickupManager.HostConsume(Resolve(pickup));

        /// <summary>Accepts the pickup component or the object carrying it, so a caller that holds either does not
        /// have to know which one this wanted.</summary>
        private static Pickup Resolve(Component pickup)
        {
            if (pickup == null) return null;
            return pickup as Pickup ?? pickup.GetComponent<Pickup>();
        }
    }
}
