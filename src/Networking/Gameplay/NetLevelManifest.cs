using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.3-E: Host-authoritative semantic level manifest.
    /// This is NOT a full Unity world serialization — it is the host's generation RESULT summary
    /// (seed, rooms, units, special events) so the client can diff its provisional local world
    /// against the host's authoritative one, quarantine client-only combat enemies, and bind
    /// host enemies to the correct local instances before runtime sync takes over.
    /// </summary>
    internal sealed class NetLevelManifestHeader
    {
        public int    ManifestVersion    { get; set; } = 1;
        public string Role               { get; set; } = "";   // "Host" / "Client"
        public string SceneName          { get; set; } = "";
        public int    LevelIndex         { get; set; } = -1;
        public bool   HasLevelSeed       { get; set; }
        public int    LevelSeed          { get; set; }
        public int    GenerationRevision { get; set; }
        public int    RoomCount          { get; set; }
        public int    UnitCount          { get; set; }
        public int    CombatEnemyCount   { get; set; }
        public int    SpecialEventCount  { get; set; }
        // Phase 5.3-F: split hash. GenerationHash covers structural generation result only
        // (seed, rooms, unit identity/spawn/modifier) and should be stable across host/client for
        // the same generated world. RuntimeHash includes volatile state (health, live position,
        // dead/alive) and is allowed to differ.
        public uint   GenerationHash     { get; set; }
        public uint   RuntimeHash        { get; set; }
        public float  BuiltAt            { get; set; }
    }

    internal sealed class NetLevelManifestRoom
    {
        public int     RoomIndex   { get; set; }
        public string  RoomName    { get; set; } = "";
        public bool    HasPosition { get; set; }
        public Vector3 Position    { get; set; }
        public int     ChildCount  { get; set; }
    }

    internal sealed class NetLevelManifestUnit
    {
        public int     ManifestIndex        { get; set; }
        public int     SpawnIndex           { get; set; }
        public string  UnitIdentifier       { get; set; } = "";
        public string  ActorName            { get; set; } = "";
        public string  GameObjectName       { get; set; } = "";
        public int     SyncCategory         { get; set; }
        public string  Category             { get; set; } = "";
        public bool    IsCombatEnemy        { get; set; }
        public bool    HasPosition          { get; set; }
        public Vector3 Position             { get; set; }
        // Phase 5.3-G: stable spawn position for the generation hash (Position drifts at runtime).
        public bool    HasInitialPosition   { get; set; }
        public Vector3 InitialPosition      { get; set; }
        // Special-attribute markers — "Offensive", "Defensive", "Elite", combos, or "".
        public string  ModifierFlags        { get; set; } = "";
        public uint    ComponentFingerprint { get; set; }
        public bool    IsDead               { get; set; }
    }

    internal sealed class NetLevelManifestSpecial
    {
        public string  Type         { get; set; } = "";   // Trader / Ghost / EventNpc / ...
        public string  Name         { get; set; } = "";
        public int     SyncCategory { get; set; }
        public bool    HasPosition  { get; set; }
        public Vector3 Position     { get; set; }
    }

    internal sealed class NetLevelManifest
    {
        public NetLevelManifestHeader        Header   { get; set; } = new NetLevelManifestHeader();
        public List<NetLevelManifestRoom>    Rooms    { get; set; } = new List<NetLevelManifestRoom>();
        public List<NetLevelManifestUnit>    Units    { get; set; } = new List<NetLevelManifestUnit>();
        public List<NetLevelManifestSpecial> Specials { get; set; } = new List<NetLevelManifestSpecial>();

        public string SceneKey => string.IsNullOrWhiteSpace(Header.SceneName)
            ? "<unknown>:-1"
            : $"{Header.SceneName}:{Header.LevelIndex}";
    }
}
