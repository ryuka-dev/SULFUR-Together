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
        public const int ApiVersion = 2;

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

        // ------------------------------------------------------------------ naming a mirrored unit
        //
        // Mirroring copies the spawn, not everything the spawner then did to it. A mod that strengthens, shrinks,
        // recolours or otherwise alters a unit after spawning it applies that on the host, and the puppet on each
        // client is a plain instance of the same UnitSO — right kind, right place, wrong appearance. ST cannot fix
        // that generally: it does not know which of the thousands of things a mod might change are worth carrying,
        // and a blanket state mirror would fight the host-driven puppet pipeline.
        //
        // What it can do is lend the mod a name. ST already assigns every tracked unit a spawn index and already
        // binds host index to local puppet on each client — that is how host damage, death and state reach the right
        // object. These two calls expose that same pair of lookups so a mod can say "this unit, the one you know as
        // N" over its own channel, and have every peer apply its own change to its own copy.
        //
        // The id is meaningful only within one level and one session; it is not a save-safe identifier and must not
        // be stored as one. A mod addressing a unit by id should carry its own type guard alongside it — the unit
        // definition it expects — exactly as ST's own addressed channels do, so that an id which has been recycled
        // cannot deliver a change to the wrong unit.

        /// <summary>
        /// Host: the session's id for a unit, or 0 when it has none yet or this peer is not tracking it.
        /// </summary>
        /// <param name="unit">The object carrying the game's <c>Unit</c>/<c>Npc</c>, or the component itself.</param>
        public static int GetSpawnId(Component unit)
        {
            if (unit == null) return 0;
            return SULFURTogether.Networking.Gameplay.NetGameplayProbeManager
                .TryGetHostEntityBinding(unit, out int spawnIndex, out _) ? spawnIndex : 0;
        }

        /// <summary>
        /// Client: the local puppet bound to a host spawn id, or null when nothing is bound to it yet.
        /// </summary>
        /// <remarks>
        /// Null is an ordinary answer, not a failure: a mirror spawn is asynchronous, so a message about a unit can
        /// arrive before the unit does. A caller should keep the change pending and try again for a second or two
        /// rather than dropping it.
        /// </remarks>
        public static Component ResolveSpawn(int spawnId)
        {
            if (spawnId <= 0) return null;
            return SULFURTogether.Networking.Gameplay.NetGameplayProbeManager
                .TryGetHostBoundRuntimeObject(spawnId, out object runtime) ? runtime as Component : null;
        }
    }
}
