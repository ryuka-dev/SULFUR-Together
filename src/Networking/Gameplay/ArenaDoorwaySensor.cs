using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2e — counts how many times THIS end's local player physically traverses a combat-room doorway, so
    /// in/out is a pure event (no distance / no arena-volume assumption — see the rejected radius + trigger-containment
    /// attempts). Attached by <see cref="ArenaLockdownManager"/> to a seal <c>PlayerTrigger</c>'s GameObject (its trigger
    /// collider survives the vanilla <c>onlyOnce</c>, so it keeps firing). Each full pass-through toggles parity: odd
    /// crossings = inside, even = outside. Only the local player counts; remote ghost proxies are ignored by root match.
    /// <para>The player has SEVERAL colliders under its root (body / feet / hitboxes), so one walk-through fires several
    /// Enter/Exit pairs. We refcount overlapping player colliders and toggle once per "within period" (count returns to
    /// 0), plus a short debounce, so one pass = one toggle (Log161 showed a single entry double-counted → in→out).</para>
    /// </summary>
    internal sealed class ArenaDoorwaySensor : MonoBehaviour
    {
        public string  ArenaKey;
        public Vector3 ArenaPos;

        private int   _overlap;        // # of local-player colliders currently inside the doorway volume
        private float _lastToggleTime; // debounce: collapse rapid multi-collider toggles from one pass

        private const float DebounceSeconds = 0.5f;

        private void OnTriggerEnter(Collider other)
        {
            if (IsLocalPlayer(other)) _overlap++;
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsLocalPlayer(other)) return;
            _overlap--;
            if (_overlap > 0) return;     // some player collider is still within — not a full exit yet
            _overlap = 0;

            float now = Time.unscaledTime;
            if (now - _lastToggleTime < DebounceSeconds) return; // same pass, already counted
            _lastToggleTime = now;
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
