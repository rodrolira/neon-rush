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
            _buffered = SwipeCommand.None;
            _time = 0f;
            _lean = 0f;
            _previousX = 0f;

            _transform.localPosition = Vector3.zero;
            _transform.localRotation = Quaternion.identity;
        }

        /// <summary>Advances the player by one frame.</summary>
        public void Tick(float deltaTime, SwipeCommand command)
        {
            _time += deltaTime;

            ApplyCommand(command);
            TickLane(deltaTime);
            TickVertical(deltaTime);
            TickSlide(deltaTime);

            var x = Mathf.Lerp(
                _laneFrom.OffsetFor(_tuning.LaneWidth),
                _lane.OffsetFor(_tuning.LaneWidth),
                Smooth(_laneBlend));

            _transform.localPosition = new Vector3(x, _height, 0f);

            TickLean(deltaTime, x);
        }

        // -------------------------------------------------------------------------------
        // Input buffering
        // -------------------------------------------------------------------------------

        /// <summary>
        /// How long an unusable command is remembered, in seconds.
        ///
        /// This is the single highest-leverage feel setting in the game. Without it, a player who
        /// swipes down a few frames before landing gets *nothing* — the input arrives while they
        /// are airborne, the slide is refused, and the command evaporates. They do not experience
        /// that as "I was slightly early". They experience it as "I swiped and the game ignored
        /// me", and then they die and blame the controls.
        ///
        /// 0.15s is roughly the window a human perceives as "the same moment". Much longer and
        /// stale inputs start firing after the player has changed their mind, which feels equally
        /// broken in the opposite direction.
        /// </summary>
        private const float BufferWindow = 0.15f;

        private SwipeCommand _buffered;
        private float _bufferExpiry;
        private float _time;

        private void ApplyCommand(SwipeCommand command)
        {
            if (command != SwipeCommand.None)
            {
                // A fresh command always supersedes a buffered one. If the player swiped up and
                // then immediately swiped left, they want to go left; replaying the stale jump
                // afterwards would be the game overriding a decision they already changed.
                if (TryExecute(command, fresh: true))
                {
                    _buffered = SwipeCommand.None;
                }
                else
                {
                    _buffered = command;
                    _bufferExpiry = _time + BufferWindow;
                }

                return;
            }

            if (_buffered == SwipeCommand.None) return;

            if (_time > _bufferExpiry)
            {
                _buffered = SwipeCommand.None;
                return;
            }

            if (TryExecute(_buffered, fresh: false))
            {
                _buffered = SwipeCommand.None;
            }
        }

        /// <summary>
        /// Attempts a command. Returns true when it was consumed (acted on, or legitimately
        /// discarded); false when it could not be honoured yet and should be buffered.
        /// </summary>
        /// <param name="fresh">
        /// True on the frame the player actually swiped. Some effects — the airborne fast-fall —
        /// must fire once on the real input and never again as the buffered retry replays.
        /// </param>
        private bool TryExecute(SwipeCommand command, bool fresh)
        {
            switch (command)
            {
                case SwipeCommand.Left:
                    TryChangeLane(-1);
                    // Consumed even at the edge of the track. Buffering an edge-blocked swipe would
                    // fire it later, moving the player into a lane they asked for a moment ago and
                    // no longer want — a surprise lane change is worse than a missed one.
                    return true;

                case SwipeCommand.Right:
                    TryChangeLane(1);
                    return true;

                case SwipeCommand.Up:
                    if (!IsGrounded) return false; // Airborne: buffer it, jump the instant we land.

                    TryJump();
                    return true;

                case SwipeCommand.Down:
                    if (IsGrounded)
                    {
                        TrySlide();
                        return true;
                    }

                    // Airborne. The swipe means two things at once: "come down now" and "slide when
                    // I get there". Fire the fast-fall on the real input only, then buffer the slide
                    // so the player lands already sliding — which is what they intended and what
                    // makes jump-then-slide sequences possible at speed.
                    if (fresh) FastFall();

                    return false;

                case SwipeCommand.None:
                default:
                    return true;
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
            if (!IsGrounded) return; // Airborne slides are handled by the buffer; see TryExecute.

            _slideRemaining = _tuning.SlideDuration;
            _bus.Publish(new PlayerSlid());
        }

        /// <summary>
        /// Drives the player back to the ground immediately.
        ///
        /// Without this, a jump commits the player to a fixed ~0.53s arc. Jump over one obstacle,
        /// find a barrier you must slide under right behind it, and you are stuck floating with no
        /// legal move. A runner must never take away the player's last option.
        /// </summary>
        private void FastFall()
        {
            _verticalVelocity = -_tuning.JumpVelocity;
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

        // -------------------------------------------------------------------------------
        // Lean
        // -------------------------------------------------------------------------------

        /// <summary>Maximum body roll during a lane change, in degrees.</summary>
        private const float MaxLean = 18f;

        /// <summary>Degrees of roll per metre/second of lateral velocity.</summary>
        private const float LeanPerLateralSpeed = 1.4f;

        /// <summary>How fast the lean settles. Higher = snappier.</summary>
        private const float LeanSharpness = 14f;

        private float _lean;
        private float _previousX;

        /// <summary>
        /// Rolls the player into their lane change, proportional to how fast they are actually
        /// moving sideways.
        ///
        /// This is pure presentation and it is worth stating why it earns its keep: the lane change
        /// itself takes 0.15s, which is too fast to read as motion. Without a lean the player just
        /// *appears* in the next lane. The roll is what tells the eye that a movement happened and
        /// which direction it went.
        ///
        /// It does NOT affect the hitbox: <see cref="Bounds"/> is derived from position only, never
        /// from rotation. A player can never be killed by leaning into an obstacle they did not
        /// actually touch.
        /// </summary>
        private void TickLean(float deltaTime, float x)
        {
            if (deltaTime <= 0f) return;

            var lateralVelocity = (x - _previousX) / deltaTime;
            _previousX = x;

            var target = Mathf.Clamp(lateralVelocity * LeanPerLateralSpeed, -MaxLean, MaxLean);

            // Framerate-independent smoothing (see RunCameraRig for why the naive Lerp is wrong).
            var t = 1f - Mathf.Exp(-LeanSharpness * deltaTime);
            _lean = Mathf.Lerp(_lean, target, t);

            // Negated: moving right (+x) should roll the body to the right, which is -Z in Unity's
            // left-handed rotation about the forward axis.
            _transform.localRotation = Quaternion.Euler(0f, 0f, -_lean);
        }

        /// <summary>
        /// Smoothstep. A linear lane change starts and stops abruptly and reads as robotic; easing
        /// the ends is most of what makes the movement feel like a person and not a spreadsheet.
        /// </summary>
        private static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
