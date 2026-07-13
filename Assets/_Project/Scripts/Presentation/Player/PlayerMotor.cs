using NeonRush.Application.Events;
using NeonRush.Core.Events;
using NeonRush.Domain.Run;
using NeonRush.Presentation.Input;
using UnityEngine;

namespace NeonRush.Presentation.Player
{
    /// <summary>
    /// Moves the player: lane changes, jumping, sliding.
    ///
    /// The player never moves forward. The world scrolls toward them (see TrackStreamer), so this
    /// only ever touches X (lane) and Y (jump/slide). Two things follow from that, and both are
    /// the reason the design was chosen:
    ///
    ///  · Z stays at 0 forever, so a 20 km run has exactly the same floating-point precision as
    ///    the first metre. A player who moved forward would be at z = 20000 by then, where a
    ///    32-bit float's resolution has degraded to roughly a millimetre and jitter starts showing
    ///    up in the camera and in collision tests.
    ///  · There is no origin-rebasing code, because there is no origin drift to fix.
    ///
    /// Deliberately not a MonoBehaviour: it is ticked once, in order, by the game loop. Three
    /// hundred obstacles being ticked by one system beats three hundred Update() callbacks, each
    /// of which pays Unity's managed-to-native transition.
    /// </summary>
    public sealed class PlayerMotor
    {
        private readonly Transform _transform;
        private readonly RunTuning _tuning;
        private readonly IEventBus _bus;

        /// <summary>Standing height of the player box, in metres. Used to derive the slide collider.</summary>
        private readonly float _standingHeight;

        private Lane _lane;
        private Lane _laneFrom;

        /// <summary>0..1 progress of the current lane slide. 1 = settled.</summary>
        private float _laneBlend = 1f;

        private float _verticalVelocity;
        private float _height;         // y of the player's centre while airborne, 0 = grounded
        private float _slideRemaining;

        public PlayerMotor(Transform transform, RunTuning tuning, IEventBus bus, float standingHeight)
        {
            _transform = transform;
            _tuning = tuning;
            _bus = bus;
            _standingHeight = standingHeight;

            _lane = Lane.Centre;
            _laneFrom = Lane.Centre;
        }

        public Lane CurrentLane => _lane;

        public bool IsGrounded => _height <= 0.0001f;

        public bool IsSliding => _slideRemaining > 0f;

        /// <summary>Current collider height: shrunk while sliding so the player passes under barriers.</summary>
        public float CurrentHeight => IsSliding ? _standingHeight * _tuning.SlideHeightFactor : _standingHeight;

        /// <summary>
        /// The player's world-space bounding box this frame. The collision system tests this
        /// against obstacles — there are no Unity colliders anywhere in the game.
        /// </summary>
        public Bounds Bounds
        {
            get
            {
                var height = CurrentHeight;
                var position = _transform.localPosition;

                // The box is anchored at the player's feet, so shrinking it while sliding lowers
                // the top edge and leaves the bottom on the ground — which is exactly what a slide
                // must do to duck under a barrier.
                var centre = new Vector3(position.x, position.y + height * 0.5f, position.z);
                return new Bounds(centre, new Vector3(0.8f, height, 0.8f));
            }
        }

        /// <summary>Resets the player to the centre lane, grounded, not sliding. Called at the start of every run.</summary>
        public void Reset()
        {
            _lane = Lane.Centre;
            _laneFrom = Lane.Centre;
            _laneBlend = 1f;
            _verticalVelocity = 0f;
            _height = 0f;
            _slideRemaining = 0f;

            _transform.localPosition = Vector3.zero;
        }

        /// <summary>Advances the player by one frame.</summary>
        public void Tick(float deltaTime, SwipeCommand command)
        {
            ApplyCommand(command);
            TickLane(deltaTime);
            TickVertical(deltaTime);
            TickSlide(deltaTime);

            var x = Mathf.Lerp(
                _laneFrom.OffsetFor(_tuning.LaneWidth),
                _lane.OffsetFor(_tuning.LaneWidth),
                Smooth(_laneBlend));

            _transform.localPosition = new Vector3(x, _height, 0f);
        }

        private void ApplyCommand(SwipeCommand command)
        {
            switch (command)
            {
                case SwipeCommand.Left:
                    TryChangeLane(-1);
                    break;

                case SwipeCommand.Right:
                    TryChangeLane(1);
                    break;

                case SwipeCommand.Up:
                    TryJump();
                    break;

                case SwipeCommand.Down:
                    TrySlide();
                    break;

                case SwipeCommand.None:
                default:
                    break;
            }
        }

        private void TryChangeLane(int direction)
        {
            if (!_lane.CanStep(direction))
            {
                return; // Already at the edge. Clamp rather than wrap — see LaneExtensions.
            }

            // Lane changes are allowed mid-air and mid-slide on purpose. Forbidding them would mean
            // a player who jumps an obstacle and lands on a second one in the same lane has no way
            // out, and dies to a situation with no legal solution. Runners must always leave the
            // player one legal escape.
            var next = direction < 0 ? _lane.StepLeft() : _lane.StepRight();

            // Re-anchor the interpolation to where the player actually IS right now, not to the
            // lane they nominally came from. Without this, swiping twice in quick succession snaps
            // them back to the previous lane's centre before continuing, which reads as a stutter.
            _laneFrom = CurrentVisualLane();
            _lane = next;
            _laneBlend = 0f;

            _bus.Publish(new LaneChanged(_laneFrom, _lane));
        }

        /// <summary>
        /// The lane nearest the player's actual on-screen X. Used to re-anchor a lane change that
        /// interrupts another one already in flight.
        /// </summary>
        private Lane CurrentVisualLane()
        {
            var x = _transform.localPosition.x;
            var raw = Mathf.RoundToInt(x / _tuning.LaneWidth);
            return (Lane)Mathf.Clamp(raw, LaneExtensions.MinLane, LaneExtensions.MaxLane);
        }

        private void TryJump()
        {
            if (!IsGrounded) return; // No double jump. Deliberate: it trivialises obstacle design.

            _slideRemaining = 0f; // Jumping cancels a slide.
            _verticalVelocity = _tuning.JumpVelocity;

            _bus.Publish(new PlayerJumped());
        }

        private void TrySlide()
        {
            if (IsSliding) return;

            if (!IsGrounded)
            {
                // Swipe-down in the air is a fast-fall, not a slide. This is a small thing that
                // players notice immediately: without it, jumping over one obstacle and needing to
                // slide under the next feels impossible, because you are stuck floating.
                _verticalVelocity = -_tuning.JumpVelocity;
                return;
            }

            _slideRemaining = _tuning.SlideDuration;
            _bus.Publish(new PlayerSlid());
        }

        private void TickLane(float deltaTime)
        {
            if (_laneBlend >= 1f) return;

            _laneBlend += deltaTime / _tuning.LaneChangeDuration;

            if (_laneBlend > 1f)
            {
                _laneBlend = 1f;
                _laneFrom = _lane;
            }
        }

        private void TickVertical(float deltaTime)
        {
            if (IsGrounded && _verticalVelocity <= 0f)
            {
                _height = 0f;
                _verticalVelocity = 0f;
                return;
            }

            _verticalVelocity -= _tuning.Gravity * deltaTime;
            _height += _verticalVelocity * deltaTime;

            if (_height <= 0f)
            {
                _height = 0f;
                _verticalVelocity = 0f;
            }
        }

        private void TickSlide(float deltaTime)
        {
            if (_slideRemaining <= 0f) return;

            _slideRemaining -= deltaTime;

            if (_slideRemaining < 0f) _slideRemaining = 0f;
        }

        /// <summary>
        /// Smoothstep. A linear lane change starts and stops abruptly and reads as robotic; easing
        /// the ends is most of what makes the movement feel like a person and not a spreadsheet.
        /// </summary>
        private static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
