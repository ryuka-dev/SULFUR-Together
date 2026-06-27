using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-1b combat-room door sync for the <c>GameObject.SetActive</c> variant (Lucia etc.).
    /// <para>Some arenas don't use a <see cref="GateSyncManager">MetalGate</see>; instead a <c>PlayerTrigger</c> the
    /// entering player crosses fires <c>GameObject.SetActive(Doors, true)</c> via its <c>onTriggerEvents</c> (alongside
    /// <c>Npc.Interact</c> for the dialog + music). Like gates these are per-end independent, so an out-of-room player's
    /// door is left inactive.</para>
    /// <para>The key is the firing <c>PlayerTrigger</c>'s world position (static, deterministic). The receiver finds the
    /// matching local trigger and reads ITS OWN <c>onTriggerEvents</c> to get its local door GameObject reference (works
    /// even while the door is inactive — persistent UnityEvent targets are serialized references), then SetActives it to
    /// the broadcast state. Door targets are matched by name (only objects whose name contains "door").</para>
    /// </summary>
    internal sealed class NetTriggerDoors
    {
        public string PeerId { get; set; } = "";

        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // Deterministic world-position key of the firing PlayerTrigger.
        public Vector3 TriggerPos { get; set; }

        // Door GameObject SetActive targets fired by the trigger: name -> desired active state.
        public List<DoorEntry> Doors { get; set; } = new List<DoorEntry>();

        public struct DoorEntry { public string Name; public bool Active; }

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
