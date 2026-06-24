using System;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Tracks only the local player's Transform so we can publish visual-only transform packets.
    /// It never stores or controls remote gameplay Player/Unit objects.
    /// </summary>
    public sealed class NetLocalPlayerTracker
    {
        private Transform? _transform;
        private string     _sourceDescription = "<none>";

        public bool HasTransform => _transform != null;
        /// <summary>The local player's transform, or null. Used by soft player-vs-player collision to nudge the player out of overlap.</summary>
        public Transform? LocalTransform => _transform;

        public void Clear()
        {
            _transform = null;
            _sourceDescription = "<none>";
            ResetPlayerRefs();
        }

        public bool TrySetLocalPlayerObject(object? player, out string description)
        {
            description = "<null>";
            var transform = ExtractTransform(player);
            if (transform == null)
                return false;

            _transform = transform;
            _sourceDescription = $"{transform.name}#{transform.GetInstanceID()}";
            description = _sourceDescription;
            ResetPlayerRefs();
            return true;
        }

        public bool TryBuildState(string peerId, string playerName, NetRunState runState, int sequence, float now, out NetPlayerTransformState state)
        {
            state = new NetPlayerTransformState();
            if (_transform == null) return false;
            if (!runState.HasLevel) return false;

            try
            {
                state.PeerId      = string.IsNullOrWhiteSpace(peerId) ? runState.PeerId : peerId;
                state.PlayerName  = string.IsNullOrWhiteSpace(playerName) ? runState.PlayerName : playerName;
                state.ChapterName  = runState.ChapterName;
                state.LevelIndex   = runState.LevelIndex;
                state.HasLevelSeed = runState.HasLevelSeed;
                state.LevelSeed    = runState.LevelSeed;
                state.Sequence     = sequence;
                state.SentAt      = now;
                state.Position    = _transform.position;
                state.RotationY   = _transform.rotation.eulerAngles.y;
                state.LookYaw     = ResolveCameraYaw(state.RotationY);
                state.LookPitch   = ResolveCameraPitch();
                state.Moving      = ResolveInputMoving();
                return true;
            }
            catch
            {
                _transform = null;
                _sourceDescription = "<lost>";
                return false;
            }
        }

        public string FormatStatus()
        {
            return _transform == null ? "localPlayer=<none>" : $"localPlayer={_sourceDescription}";
        }

        // First-person: the look direction is the player CAMERA's heading (Player.playerCamera), not the body root yaw.
        // Movement for the walk animation is the player's INPUT magnitude (Player.inputReader.GetMovementInput()), so
        // being pushed/sliding doesn't animate. Both are read from the Player component on the local player root.
        private object?      _playerComponent;
        private Camera?      _playerCamera;
        private FieldInfo?   _inputReaderField;
        private object?      _inputReader;
        private MethodInfo?  _getMovementInputMi;
        private bool         _playerRefsResolved;

        private void ResetPlayerRefs()
        {
            _playerRefsResolved = false;
            _playerComponent = null;
            _playerCamera = null;
            _inputReader = null;
            _getMovementInputMi = null;
        }

        private void EnsurePlayerRefs()
        {
            if (_playerRefsResolved || _transform == null) return;
            _playerRefsResolved = true;
            try
            {
                var playerType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Player");
                if (playerType == null) return;
                _playerComponent = _transform.GetComponent(playerType);
                if (_playerComponent == null) return;

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var camField = playerType.GetField("playerCamera", F);
                _playerCamera = camField?.GetValue(_playerComponent) as Camera;

                _inputReaderField = playerType.GetField("inputReader", F);
                _inputReader = _inputReaderField?.GetValue(_playerComponent);
                if (_inputReader != null)
                    _getMovementInputMi = _inputReader.GetType().GetMethod("GetMovementInput", Type.EmptyTypes);
            }
            catch { }
        }

        private float ResolveCameraYaw(float fallback)
        {
            try
            {
                EnsurePlayerRefs();
                Camera cam = (_playerCamera != null) ? _playerCamera : Camera.main;
                if (cam == null) return fallback;
                Vector3 f = cam.transform.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-6f) return fallback;
                return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; // heading (robust to camera pitch)
            }
            catch { return fallback; }
        }

        private float ResolveCameraPitch()
        {
            try
            {
                EnsurePlayerRefs();
                Camera cam = (_playerCamera != null) ? _playerCamera : Camera.main;
                if (cam == null) return 0f;
                Vector3 f = cam.transform.forward;
                // Euler-X convention: +pitch = looking DOWN. forward.y > 0 = looking up → negative pitch.
                return -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
            catch { return 0f; }
        }

        private bool ResolveInputMoving()
        {
            try
            {
                EnsurePlayerRefs();
                if (_getMovementInputMi == null || _inputReader == null) return false;
                object r = _getMovementInputMi.Invoke(_inputReader, null);
                if (r is Vector2 v) return v.sqrMagnitude > 0.02f; // ~0.14 magnitude deadzone
            }
            catch { }
            return false;
        }

        private static Transform? ExtractTransform(object? player)
        {
            if (player == null) return null;

            if (player is Transform t) return t;
            if (player is GameObject go) return go.transform;
            if (player is Component c) return c.transform;

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = player.GetType().GetProperty("transform", flags);
                if (prop != null && prop.GetValue(player, null) is Transform pt)
                    return pt;

                var field = player.GetType().GetField("transform", flags);
                if (field != null && field.GetValue(player) is Transform ft)
                    return ft;

                var goProp = player.GetType().GetProperty("gameObject", flags);
                if (goProp != null && goProp.GetValue(player, null) is GameObject pgo)
                    return pgo.transform;
            }
            catch { }

            return null;
        }
    }
}
