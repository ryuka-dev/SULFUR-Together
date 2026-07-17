using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-5b: a host-authoritative Endless XP pickup (host → all clients, ReliableOrdered). When an enemy dies the
    /// host assigns a <see cref="DropId"/> and broadcasts the drop; both ends spawn the same visual orbs (cosmetic,
    /// worth 0 so vanilla collection never credits) and register a pending pickup at <see cref="Position"/>. When a
    /// player walks within pickup range they ask the host to collect it (first-collector-wins), the host confirms with
    /// <see cref="NetEndlessXpCollect"/>, and both ends remove the orbs. The reward differs by mode: Independent = only
    /// the collector's local pool gains <see cref="TotalXp"/>; Shared = the host's single pool gains it (mirrored to all
    /// via the wave-state snapshot). Carries the run context so a client in a different level ignores it.
    /// </summary>
    internal sealed class NetEndlessXpDrop
    {
        public string  ChapterName  { get; set; } = "";
        public int     LevelIndex   { get; set; } = -1;
        public bool    HasLevelSeed { get; set; }
        public int     LevelSeed    { get; set; }

        public int     DropId   { get; set; }  // host-authoritative unique id for this pickup
        public Vector3 Position { get; set; }
        public int     TotalXp  { get; set; }  // total XP the pickup is worth (ExperienceOnKill)
        public int     OrbCount { get; set; }  // number of visual orbs to spawn (cosmetic)

        // EM-5c: empty = Shared-mode pickup (walk-over, host pool). Non-empty = Independent-mode award: the orbs are
        // spawned only on this peer's screen (real value) and fly straight to them regardless of distance.
        public string  AwardPeerId { get; set; } = "";
    }
}
