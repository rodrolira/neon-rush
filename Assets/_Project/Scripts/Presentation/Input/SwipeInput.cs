using UnityEngine;
using UnityEngine.InputSystem;

namespace NeonRush.Presentation.Input
{
    /// <summary>The four gestures the runner understands, plus "nothing happened this frame".</summary>
    public enum SwipeCommand
    {
        None = 0,
        Left = 1,
        Right = 2,
        Up = 3,
        Down = 4,
    }

    /// <summary>
    /// Turns touches (and, in the Editor, arrow keys) into one <see cref="SwipeCommand"/> per frame.
    ///
    /// Two decisions here are what make the controls feel good, and both are worth defending:
    ///
    /// 1. <b>The swipe fires the moment the finger crosses the distance threshold, not when it is
    ///    lifted.</b> Waiting for touch-up adds 80-150 ms of latency depending on how leisurely the
    ///    player's thumb is. That delay is well above the ~100 ms at which humans start perceiving
    ///    input as laggy, and players do not report it as latency — they report it as "I swiped and
    ///    it didn't move" and they blame the game for their death. This single choice is the
    ///    difference between controls that feel crisp and controls that feel broken.
    ///
    /// 2. <b>The gesture is consumed for the rest of the touch.</b> Without this, one long drag
    ///    would emit Left on every frame it stayed past the threshold, and the player would slide
    ///    across three lanes from a single swipe.
    ///
    /// The threshold is expressed as a fraction of screen height, not in pixels, because a 60 px
    /// swipe is a flick on a 720p phone and a twitch on a 1440p flagship.
    /// </summary>
    public sealed class SwipeInput
    {
        /// <summary>Minimum travel to register a swipe, as a fraction of the shorter screen edge.</summary>
        private const float ThresholdFraction = 0.04f;

        /// <summary>
        /// How much longer the dominant axis must be than the other before we commit to it.
        /// A diagonal drag is ambiguous; guessing wrong kills the player. 1.2 means "clearly more
        /// horizontal than vertical" (or vice versa) before we act.
        /// </summary>
        private const float AxisDominance = 1.2f;

        private Vector2 _touchStart;
        private bool _touchActive;
        private bool _consumed;

        /// <summary>Polls input and returns the gesture for this frame, or <see cref="SwipeCommand.None"/>.</summary>
        public SwipeCommand Poll()
        {
            var keyboard = PollKeyboard();
            if (keyboard != SwipeCommand.None) return keyboard;

            return PollTouch();
        }

        /// <summary>
        /// Arrow keys / WASD. This is not debug scaffolding — it is how the game is playable in the
        /// Editor, which is where every designer and QA pass actually happens. Shipping without it
        /// means nobody can test a build without deploying to a phone first.
        /// </summary>
        private static SwipeCommand PollKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return SwipeCommand.None;

            if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame) return SwipeCommand.Left;
            if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame) return SwipeCommand.Right;
            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame) return SwipeCommand.Up;
            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame) return SwipeCommand.Down;

            return SwipeCommand.None;
        }

        private SwipeCommand PollTouch()
        {
            if (!TryGetPointer(out var position, out var isDown))
            {
                _touchActive = false;
                _consumed = false;
                return SwipeCommand.None;
            }

            if (!isDown)
            {
                _touchActive = false;
                _consumed = false;
                return SwipeCommand.None;
            }

            if (!_touchActive)
            {
                _touchActive = true;
                _consumed = false;
                _touchStart = position;
                return SwipeCommand.None;
            }

            if (_consumed) return SwipeCommand.None;

            var delta = position - _touchStart;

            // Fraction of the *shorter* edge, so a swipe feels the same distance in portrait and
            // landscape and on any DPI.
            var threshold = Mathf.Min(Screen.width, Screen.height) * ThresholdFraction;

            var absX = Mathf.Abs(delta.x);
            var absY = Mathf.Abs(delta.y);

            if (absX < threshold && absY < threshold)
            {
                return SwipeCommand.None; // Still a tap, or still travelling.
            }

            if (absX > absY * AxisDominance)
            {
                _consumed = true;
                return delta.x > 0f ? SwipeCommand.Right : SwipeCommand.Left;
            }

            if (absY > absX * AxisDominance)
            {
                _consumed = true;
                return delta.y > 0f ? SwipeCommand.Up : SwipeCommand.Down;
            }

            // Too diagonal to call. Do nothing and let the finger keep travelling — the player will
            // resolve the ambiguity themselves within a few milliseconds. Guessing here is worse
            // than waiting.
            return SwipeCommand.None;
        }

        /// <summary>
        /// Reads the primary pointer. Touchscreen first, mouse second, so the same code path serves
        /// a phone and an Editor play session.
        /// </summary>
        private static bool TryGetPointer(out Vector2 position, out bool isDown)
        {
            var touchscreen = Touchscreen.current;
            if (touchscreen != null)
            {
                var touch = touchscreen.primaryTouch;
                position = touch.position.ReadValue();
                isDown = touch.press.isPressed;
                return true;
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                position = mouse.position.ReadValue();
                isDown = mouse.leftButton.isPressed;
                return true;
            }

            position = default;
            isDown = false;
            return false;
        }
    }
}
