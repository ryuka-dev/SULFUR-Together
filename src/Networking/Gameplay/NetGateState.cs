using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-1 generic combat-room gate sync — one event per <c>MetalGate</c> open/close on the firing peer.
    /// <para>MetalGates are static scene/level-gen doodads placed deterministically (host-authoritative seed, synced), so
    /// both ends own the same set of gates at the same world positions. The key is the gate's world position (gates never
    /// move). Each end's local <c>PlayerTrigger</c> only closes its OWN gate (gates are per-end independent), so without
    /// this an out-of-room / AFK player's gate is left in the wrong state.</para>
    /// <para>Receivers find the nearest local <c>MetalGate</c> to the key and call the same <c>Close()</c>/<c>Open()</c>,
    /// reproducing whatever that gate does (animation + optional collider/navmesh). Peer-authoritative EFFECT mirror,
    /// same topology as <see cref="NetBreakableBreak"/>.</para>
    /// </summary>
    internal sealed class NetGateState
    {
        // Identity — which player changed the gate (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (so a receiver in a different level ignores it — gates at coincidentally similar positions in
        // another level must not match).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // Deterministic world-position key of the gate.
        public Vector3 Position { get; set; }

        // The new state: true = Close() fired, false = Open() fired.
        public bool Closed { get; set; }

        // TB-INTRO: what fired at Position. 0 = a MetalGate Close/Open (the original LD-1 payload). 1 = a room-event
        // PlayerTrigger with a boss-appear animation (persistent Animator.SetTrigger) fired on the sending end — the
        // receiver invokes ITS local PlayerTrigger at the same position so the boss's entrance plays for everyone (and
        // the trigger's own hasBeenTriggered is consumed, so a late-walking-in player doesn't replay it mid-fight).
        public byte Kind { get; set; }

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
            {
                if (!HasLevelSeed || !localState.HasLevelSeed) return false;
                if (localState.LevelSeed != LevelSeed) return false;
            }
            return true;
        }
    }
}
