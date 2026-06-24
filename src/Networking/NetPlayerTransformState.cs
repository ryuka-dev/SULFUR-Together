using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 3.0 visual-only player transform packet.
    /// This metadata is used only to move a local visual proxy; it is never applied to real Player/Unit objects.
    /// </summary>
    public sealed class NetPlayerTransformState
    {
        public string PeerId      { get; set; } = "";
        public string PlayerName  { get; set; } = "";
        public string ChapterName { get; set; } = "<unknown>";
        public int    LevelIndex  { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }
        public int    Sequence    { get; set; }
        public float  SentAt      { get; set; }
        public Vector3 Position   { get; set; }
        public float  RotationY   { get; set; }
        // Phase 5.6-WS-3: the player's CAMERA/look yaw (first-person — distinct from the body root yaw above). Used so
        // remote viewers know which way the player is looking (front/back billboard, weapon aim). Defaults to RotationY.
        public float  LookYaw     { get; set; }
        // Phase 5.6-WS-3: the player's camera PITCH (degrees, +down) so the held weapon can aim up/down on remote views.
        public float  LookPitch   { get; set; }
        // Phase 5.6-WS-3: whether the player is actively INPUT-walking (not pushed/sliding) — drives the walk animation
        // so it plays only on intentional movement and stops cleanly when input stops.
        public bool   Moving      { get; set; }

        public bool HasScene => !string.IsNullOrWhiteSpace(ChapterName) && ChapterName != "<unknown>" && LevelIndex >= 0;

        public string SceneCompareKey() => NetSceneName.SceneCompareKey(ChapterName, LevelIndex);

        public string ToCompactString()
        {
            string peer = string.IsNullOrWhiteSpace(PeerId) ? "?" : PeerId;
            string name = string.IsNullOrWhiteSpace(PlayerName) ? "?" : PlayerName;
            string seed = HasLevelSeed ? $"#seed={LevelSeed}" : "#seed=?";
            return $"{name}(id={peer},scene={SceneCompareKey()}{seed},seq={Sequence},pos=({Position.x:F2},{Position.y:F2},{Position.z:F2}),rotY={RotationY:F1})";
        }
    }
}
