using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.4.0-O3-B: Host-authoritative world entity record sent in HostWorldRoster packets.
    /// Client uses this to bind Host spawn indices to local entities for strict type-safe matching.
    /// </summary>
    internal sealed class NetWorldEntityRecord
    {
        public string NetEntityId    { get; set; } = "";   // stable fingerprint (chapter|level|seed|unitId|idx|qx|qy|qz)
        public int    SyncCategory   { get; set; }         // SyncCat* constant from ProbeManager
        public string Category       { get; set; } = "";
        public string UnitIdentifier { get; set; } = "";
        public string ActorName      { get; set; } = "";
        public int    SpawnIndex     { get; set; }
        public bool   HasPosition    { get; set; }
        public Vector3 Position      { get; set; }
        public int    SceneRevision  { get; set; }
        public string ChapterName    { get; set; } = "";
        public int    LevelIndex     { get; set; } = -1;
        public bool   HasLevelSeed   { get; set; }
        public int    LevelSeed      { get; set; }
    }
}
