using UnityEngine;

namespace SULFURTogether.Api
{
    /// <summary>
    /// Public, mod-neutral declaration that a destructible belongs to a mod and is replicated by that mod, so
    /// SULFUR Together's own in-scene destructible sync must leave it alone.
    ///
    /// ST mirrors the destruction of the breakables a level generates. Those exist on every peer because every
    /// peer generates the same level, so a break can be matched across peers by the position the breakable was
    /// spawned at — near-identical everywhere, with a small tolerance for float drift.
    ///
    /// A mod's own destructibles break that assumption. They are created at runtime, wherever the mod decided,
    /// often many of them in one place; the position key then matches the <i>wrong</i> one, and a break on one
    /// peer destroys an unrelated object on another — including, if the mod is mid-animation with it, one that was
    /// never meant to break at all. A mod that already replicates its own destructibles does not want this channel
    /// touching them either way.
    ///
    /// Excluding is permanent for that object and costs nothing once it is gone. It is harmless in single-player
    /// and on a client, and harmless to call more than once.
    ///
    /// Threading: call from the Unity main thread, as with every other part of this API.
    /// </summary>
    public static class NetExternalDestructibles
    {
        /// <summary>Bumped on any breaking change to this API. A companion mod may gate on it.</summary>
        public const int ApiVersion = 1;

        /// <summary>
        /// Declare that <paramref name="destructible"/> is a mod's own: ST will neither broadcast its destruction
        /// nor pick it as the local match for somebody else's. Pass the object carrying the game's
        /// <c>Breakable</c>, or the component itself.
        /// </summary>
        /// <remarks>
        /// Safe to call after the object is already alive — an exclusion also withdraws it from the live match
        /// registry it may already have joined, which is the usual case for a runtime-assembled destructible.
        /// </remarks>
        public static void Exclude(GameObject destructible)
            => SULFURTogether.Networking.Gameplay.BreakableBreakManager.ExcludeExternal(destructible);
    }
}
