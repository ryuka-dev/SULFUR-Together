using UnityEngine;
using UnityEngine.InputSystem;
using SULFURTogether.Networking;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: overlay input, polled only while the cards are visible. ALL reads go through the new Input System
    /// (<see cref="Mouse"/>/<see cref="Gamepad"/>): LogOutput376 proved this game's player build has legacy
    /// <c>UnityEngine.Input</c> fully disabled ("switched active Input handling to Input System package"), so
    /// every legacy call throws InvalidOperationException. (The mod's BepInEx KeyboardShortcut bindings still
    /// work because BepInEx routes those through its own input abstraction — raw <c>Input.*</c> does not.)
    ///
    /// Each input source is guarded independently: if an API throws anyway, that source logs ONE warning naming
    /// the exception and disables itself, while the rest of the overlay keeps working. The InputSystem-touching
    /// bodies are separate non-inlined methods so even a type-load/JIT failure surfaces at the guarded call site.
    /// </summary>
    internal static class RunStatsInputReader
    {
        private static bool _mouseBroken;
        private static bool _gamepadBroken;

        /// <summary>Carousel navigation: -1 (previous), +1 (next), or 0 (no input this frame). Wheel-up = previous,
        /// wheel-down = next; one wheel detent / button press = exactly one card.</summary>
        public static int PollDelta()
        {
            if (!_mouseBroken)
            {
                try
                {
                    float scroll = ReadMouseScrollY();
                    if (scroll > 0.01f) return -1;
                    if (scroll < -0.01f) return 1;
                }
                catch (System.Exception ex)
                {
                    _mouseBroken = true;
                    NetLogger.Warn($"[RunStatsOverlay] mouse poll failed — mouse input disabled: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (!_gamepadBroken)
            {
                try
                {
                    int delta = PollGamepadDelta();
                    if (delta != 0) return delta;
                }
                catch (System.Exception ex)
                {
                    _gamepadBroken = true;
                    NetLogger.Warn($"[RunStatsOverlay] gamepad poll failed — gamepad navigation disabled: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return 0;
        }

        /// <summary>Current pointer position in screen coordinates, for the hover-tilt hit test. False when no
        /// mouse exists (or mouse input is broken) — hover simply stays inactive then.</summary>
        public static bool TryGetPointerPosition(out Vector2 position)
        {
            position = default;
            if (_mouseBroken) return false;
            try
            {
                return ReadMousePosition(out position);
            }
            catch (System.Exception ex)
            {
                _mouseBroken = true;
                NetLogger.Warn($"[RunStatsOverlay] mouse poll failed — mouse input disabled: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static float ReadMouseScrollY()
        {
            var mouse = Mouse.current;
            return mouse != null ? mouse.scroll.ReadValue().y : 0f;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadMousePosition(out Vector2 position)
        {
            var mouse = Mouse.current;
            if (mouse == null) { position = default; return false; }
            position = mouse.position.ReadValue();
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int PollGamepadDelta()
        {
            var pad = Gamepad.current;
            if (pad == null) return 0;
            if (pad.dpad.right.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame) return 1;
            if (pad.dpad.left.wasPressedThisFrame || pad.leftShoulder.wasPressedThisFrame) return -1;
            return 0;
        }
    }
}
