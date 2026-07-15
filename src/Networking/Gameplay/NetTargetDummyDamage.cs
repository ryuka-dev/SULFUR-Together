using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// TD-1: a batch of damage a peer dealt to a shared target dummy (the 0.18 <c>DamageTracker</c> on the unlockable
    /// practice dummy). Peer-authored, relayed to everyone (Client→Host→relay to other Clients; the source peer never
    /// mirrors its own — it already showed the numbers locally). Every peer that has the dummy unlocked replays the
    /// EFFECT only (flying single-hit number + running foot total), never touching the dummy's real health, so all
    /// players share one combined total and see the same head/foot numbers.
    /// <para>Coalesced on the sender: rapid hits (high fire-rate weapons) accumulate over a short window into one
    /// message whose <see cref="Amount"/> is the summed damage — kept exact so the shared total is exact, while the
    /// message rate and the remote flying-number count stay bounded. The local shooter is untouched (full per-hit
    /// numbers, vanilla).</para>
    /// <para>Identity is the dummy's deterministic world position (authored placement / synced level seed), the same
    /// keying as <see cref="NetChestOpen"/> / <see cref="NetBreakableBreak"/>. A receiver with the dummy still LOCKED
    /// has no registered tracker (its subtree is <c>SetActive(false)</c>) and silently ignores the event — content is
    /// never revealed to a player who hasn't unlocked it.</para>
    /// </summary>
    internal sealed class NetTargetDummyDamage
    {
        // Which player dealt the batch (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (a receiver in a different level must ignore it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // Deterministic world-position key of the dummy's DamageTracker (authored placement; never moves — its Update
        // only LookAt-rotates). Receivers match their local tracker by this.
        public Vector3 Position { get; set; }

        // Summed damage in this batch: added to the shared foot total AND shown as one flying head number.
        public int Amount { get; set; }

        // DamageTypes enum (drives the number colour) + whether any hit in the batch was a crit (drives the crit anim).
        public byte DamageType { get; set; }
        public bool IsCritical { get; set; }

        // Where to spawn the flying number + hay effect (last hit point in the batch).
        public Vector3 HitPoint { get; set; }

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
