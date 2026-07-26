using System;
using UnityEngine;

namespace SULFURTogether.Api
{
    /// <summary>A live host-authoritative spawn-owner registration. Dispose to unregister.</summary>
    public interface IExternalSpawnOwnerRegistration : IDisposable { }

    /// <summary>
    /// Public, mod-neutral declaration that a component's runtime unit spawns are host-authoritative.
    ///
    /// SULFUR Together does not replicate unit <i>creation</i> in general: enemies placed by level generation
    /// exist on every peer because every peer generates the same level. A unit spawned after that — by a boss
    /// mechanic, a trigger, a developer tool, or a mod — exists only where it was spawned. ST already mirrors
    /// such spawns for the sources it knows (<c>RuntimeSpawnManager</c>): the host broadcasts them and each
    /// client spawns the same unit and binds it as a puppet the host drives. This is how a companion mod joins
    /// that list without ST having to know anything about the mod.
    ///
    /// A registered owner is a promise by the caller: <b>only the host spawns these units.</b> A mod that also
    /// spawns them on its clients will double every one of them, and each peer will be fighting a different set.
    /// Guard the spawn with the session role, or spawn from host-only code.
    ///
    /// Registering is harmless on a client and while no session is running; nothing is mirrored until this peer
    /// is the host of a live session.
    ///
    /// Threading: call from the Unity main thread, as with every other part of this API.
    /// </summary>
    public static class NetExternalSpawns
    {
        /// <summary>Bumped on any breaking change to this API. A companion mod may gate on it.</summary>
        public const int ApiVersion = 1;

        /// <summary>
        /// Declare that units <paramref name="owner"/> spawns through the game's own
        /// <c>UnitSO.SpawnUnitAsync</c> are host-authoritative and spawned on the host only, so ST should mirror
        /// them onto clients as host-driven puppets. Dispose the returned token to withdraw the declaration —
        /// units already mirrored are unaffected.
        /// </summary>
        /// <param name="owner">The behaviour passed as the <c>mono</c> argument of <c>SpawnUnitAsync</c>. This is
        /// how ST recognises the spawn; spawning through any other owner is not covered by this registration.</param>
        /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
        public static IExternalSpawnOwnerRegistration RegisterHostAuthoritativeOwner(MonoBehaviour owner)
            => SULFURTogether.Networking.NetExternalSpawnOwners.Register(owner);
    }
}
