using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2e — counts how many times THIS end's local player physically traverses a combat-room doorway, so
    /// in/out is a pure event (no distance / no arena-volume assumption — see the rejected radius + trigger-containment
    /// attempts). Attached by <see cref="ArenaLockdownManager"/> to a seal <c>PlayerTrigger</c>'s GameObject (its trigger
    /// collider survives the vanilla <c>onlyOnce</c>, so it keeps firing). Each full pass-through (Enter→Exit of the
    /// doorway volume) toggles parity: odd crossings = inside, even = outside. Only the local player counts; remote
    /// ghost proxies are ignored by root match.
    /// </summary>
    internal sealed class ArenaDoorwaySensor : MonoBehaviour
    {
        public string  ArenaKey;
        public Vector3 ArenaPos;

        private bool _playerWithin; // local player currently overlapping the doorway volume (saw Enter, awaiting Exit)

        private void OnTriggerEnter(Collider other)
        {
            if (IsLocalPlayer(other)) _playerWithin = true;
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_playerWithin || !IsLocalPlayer(other)) return;
            _playerWithin = false;
            ArenaLockdownManager.OnLocalDoorwayTraversed(ArenaKey, ArenaPos);
        }

        private static bool IsLocalPlayer(Collider other)
        {
            if (other == null) return false;
            Transform root = ArenaLockdownManager.LocalPlayerRoot();
            if (root == null) return false;
            Transform o = other.transform;
            return o.root == root || o == root || o.IsChildOf(root);
        }
    }
}
