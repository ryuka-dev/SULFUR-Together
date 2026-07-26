using System.Collections.Generic;
using UnityEngine;
using SULFURTogether.Api;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// The registry behind <see cref="NetExternalSpawns"/>: which components a companion mod has declared to be
    /// host-authoritative spawners. Owns the id set only; <c>RuntimeSpawnManager.ClassifyOwner</c> asks it, and
    /// everything downstream of that — broadcast, client mirror-spawn, puppet binding — is the same path the
    /// sources ST already knows take.
    /// </summary>
    /// <remarks>
    /// Owners are held by <see cref="Object.GetInstanceID"/> rather than by reference: an instance id is stable
    /// for the object's life, never resurrects a destroyed object, and keeps nothing alive. A mod that forgets to
    /// dispose its registration leaks one integer, and a destroyed owner simply never matches again.
    /// </remarks>
    internal static class NetExternalSpawnOwners
    {
        private static readonly object _gate = new object();
        private static readonly HashSet<int> _owners = new HashSet<int>();

        /// <summary>How many owners are currently declared (diagnostics).</summary>
        public static int Count { get { lock (_gate) return _owners.Count; } }

        public static IExternalSpawnOwnerRegistration Register(MonoBehaviour owner)
        {
            if (owner == null) throw new System.ArgumentNullException(nameof(owner));

            int id = owner.GetInstanceID();
            lock (_gate) _owners.Add(id);
            NetLogger.Info($"[ExternalSpawns] '{owner.GetType().Name}' declared host-authoritative (owners: {Count})");
            return new Registration(id);
        }

        /// <summary>Whether this spawn owner was declared host-authoritative by a companion mod.</summary>
        public static bool IsRegistered(object? owner)
        {
            if (!(owner is Object unityObject)) return false;
            lock (_gate) return _owners.Contains(unityObject.GetInstanceID());
        }

        private static void Unregister(int id)
        {
            bool removed;
            lock (_gate) removed = _owners.Remove(id);
            if (removed) NetLogger.Info($"[ExternalSpawns] owner withdrawn (owners: {Count})");
        }

        private sealed class Registration : IExternalSpawnOwnerRegistration
        {
            private int? _id;
            public Registration(int id) => _id = id;

            public void Dispose()
            {
                var id = _id;
                _id = null;
                if (id != null) Unregister(id.Value);
            }
        }
    }
}
