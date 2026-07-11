using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HZ-2 throwable-effect sync — one event per Breakable-type throwable (poison/fire flask, explosive, …) that broke
    /// on the throwing peer.
    /// <para>Unlike the in-scene <see cref="NetBreakableBreak"/> channel, a throwable is instantiated at runtime by
    /// <c>ThrowableWeapon.Throw</c> and exists ONLY on the thrower's screen — other peers have no matching local instance
    /// to break. The key is the throwing weapon's <c>ItemId.value</c> (the game's stable item registry) plus the LOCKED
    /// impact position/rotation captured at <c>Breakable.Die</c>. Receivers resolve the weapon prefab
    /// (<c>ItemDatabase[id].prefab</c>), read its <c>ThrowableWeapon.prefabToThrow</c>, instantiate that at the spot and
    /// <c>Break()</c> it, mirroring the on-break effect (ground hazard / explosion) with zero physics drift.
    /// (The thrown Breakable's own <c>UnitSO.id</c> is NOT unique — several grenades share one — and isn't spawnable via
    /// the unit database, so the weapon ItemId is the correct identity.)</para>
    /// <para>Peer-authoritative EFFECT mirror (thrower is the authority, exactly like BreakableBreak). Damage stays local
    /// per peer and is protected from double-counting by HZ-1 (the host no longer forwards DoT ticks to a client proxy).</para>
    /// </summary>
    internal sealed class NetThrowableEffect
    {
        // Identity — which player threw it (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (so a receiver in a different level ignores it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // The throwing weapon's ItemId (ushort value) — resolves the weapon prefab (→ prefabToThrow) on every peer.
        public int ItemIdValue { get; set; }

        // Locked impact position (world space) + yaw, captured when the throwable broke on the thrower.
        public Vector3 Position  { get; set; }
        public float   RotationY { get; set; }

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
