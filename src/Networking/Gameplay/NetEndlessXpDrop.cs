using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-5: an Endless enemy died and dropped XP (host → all clients, ReliableOrdered). The host fires the
    /// canonical <c>EndlessModeManager.OnEnemyDied</c> and broadcasts the drop; in Independent mode each client spawns
    /// its own XP orbs at <see cref="Position"/> (count = <see cref="Count"/>, each worth <see cref="XpValue"/>) via its
    /// local <c>XPOrbManager</c>, which collects them toward the local camera into that client's own <c>currentXP</c> —
    /// so XP stays fully personal with no per-orb wire traffic. Carries the run context so a client in a different level
    /// ignores it.
    /// </summary>
    internal sealed class NetEndlessXpDrop
    {
        public string  ChapterName  { get; set; } = "";
        public int     LevelIndex   { get; set; } = -1;
        public bool    HasLevelSeed { get; set; }
        public int     LevelSeed    { get; set; }

        public Vector3 Position { get; set; }
        public int     XpValue  { get; set; }  // per-orb value (vanilla spawns 1-value orbs)
        public int     Count    { get; set; }  // number of orbs (ExperienceOnKill, doubled on a melee-bonus kill)
    }
}
