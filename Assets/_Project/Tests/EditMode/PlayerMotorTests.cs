using NeonRush.Core.Events;
using NeonRush.Domain.Run;
using NeonRush.Presentation.Input;
using NeonRush.Presentation.Player;
using NUnit.Framework;
using UnityEngine;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for how the player responds to input.
    ///
    /// The input buffer is the highest-leverage feel feature in the game and it is entirely
    /// invisible when it works — you only notice its absence, as "I swiped and the game ignored
    /// me". That makes it exactly the kind of thing that silently regresses. So it is pinned down
    /// here.
    /// </summary>
    [TestFixture]
    public sealed class PlayerMotorTests
    {
        private const float Frame = 1f / 60f;
        private const float StandingHeight = 1.6f;

        private GameObject _go;
        private EventBus _bus;
        private RunTuning _tuning;
        private PlayerMotor _motor;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("Player");
            _bus = new EventBus();
            _tuning = new RunTuning();
            _motor = new PlayerMotor(_go.transform, _tuning, _bus, StandingHeight);
            _motor.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            _bus.Dispose();
            Object.DestroyImmediate(_go);
        }

        /// <summary>Advances the motor with no input.</summary>
        private void Idle(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                _motor.Tick(Frame, SwipeCommand.None);
            }
        }

        // -------------------------------------------------------------------------------
        // Lanes
        // -------------------------------------------------------------------------------

        [Test]
        public void SwipingChangesLane()
        {
            _motor.Tick(Frame, SwipeCommand.Right);
            Assert.That(_motor.CurrentLane, Is.EqualTo(Lane.Right));

            _motor.Tick(Frame, SwipeCommand.Left);
            Assert.That(_motor.CurrentLane, Is.EqualTo(Lane.Centre));
        }

        [Test]
        public void SwipingIntoTheWall_IsNotBufferedAndFiresLater()
        {
            // An edge-blocked swipe must be discarded, not remembered. If it were buffered, the
            // player would be dumped into a lane they asked for a moment ago and no longer want —
            // a surprise lane change is far worse than a missed one.
            _motor.Tick(Frame, SwipeCommand.Left);
            Assert.That(_motor.CurrentLane, Is.EqualTo(Lane.Left));

            _motor.Tick(Frame, SwipeCommand.Left); // into the wall
            Idle(20);

            Assert.That(_motor.CurrentLane, Is.EqualTo(Lane.Left),
                "A swipe into the edge must evaporate, never resurface later.");
        }

        [Test]
        public void LaneChangesAreAllowedMidAir()
        {
            // Forbidding them would mean a player who jumps an obstacle and lands on a second one
            // in the same lane has no legal escape.
            _motor.Tick(Frame, SwipeCommand.Up);
            Assert.That(_motor.IsGrounded, Is.False);

            _motor.Tick(Frame, SwipeCommand.Right);
            Assert.That(_motor.CurrentLane, Is.EqualTo(Lane.Right));
        }

        // -------------------------------------------------------------------------------
        // Jump
        // -------------------------------------------------------------------------------

        [Test]
        public void JumpLeavesTheGroundAndReturns()
        {
            _motor.Tick(Frame, SwipeCommand.Up);
            Assert.That(_motor.IsGrounded, Is.False);

            // Airtime is ~0.53s from JumpVelocity 8.5 / Gravity 32. 60 frames is a full second.
            Idle(60);

            Assert.That(_motor.IsGrounded, Is.True, "The player must land within a second.");
        }

        [Test]
        public void NoDoubleJump()
        {
            _motor.Tick(Frame, SwipeCommand.Up);
            Idle(5);

            var heightBefore = _go.transform.localPosition.y;
            _motor.Tick(Frame, SwipeCommand.Up); // second jump attempt, mid-air

            // The buffered jump may fire on landing, but it must not add impulse while airborne.
            Idle(3);

            Assert.That(_motor.IsGrounded, Is.False);
            Assert.That(_go.transform.localPosition.y, Is.Not.EqualTo(heightBefore).Within(0.0001f),
                "The player should still be following their original arc, not a new one.");
        }

        // -------------------------------------------------------------------------------
        // Input buffering — the reason this fixture exists
        // -------------------------------------------------------------------------------

        [Test]
        public void JumpBufferedLongBeforeLanding_Expires()
        {
            _motor.Tick(Frame, SwipeCommand.Up);
            Assert.That(_motor.IsGrounded, Is.False);

            // Swipe up again while still rising. Unbuffered, this is discarded forever.
            _motor.Tick(Frame, SwipeCommand.Up);

            // Fall to the ground.
            var frames = 0;
            while (!_motor.IsGrounded && frames < 200)
            {
                frames++;
                _motor.Tick(Frame, SwipeCommand.None);
            }

            Assert.That(_motor.IsGrounded, Is.True);

            // The buffer window is 0.15s ≈ 9 frames, and the fall took far longer than that, so the
            // stale input must have expired rather than firing a phantom jump.
            _motor.Tick(Frame, SwipeCommand.None);

            Assert.That(_motor.IsGrounded, Is.True,
                "A jump buffered many frames ago must EXPIRE, not fire late. Stale inputs that " +
                "resurface are as bad as dropped ones.");
        }

        [Test]
        public void JumpBufferedJustBeforeTouchdown_DoesFire()
        {
            // The case the buffer exists for, and the one players hit constantly: they are falling,
            // they can see they are about to land, and they swipe up a fraction early to chain the
            // next jump. That swipe must survive the last few frames of the fall.
            const float NearGround = 0.35f;

            _motor.Tick(Frame, SwipeCommand.Up);

            // Rise past the threshold...
            while (_go.transform.localPosition.y < NearGround)
            {
                _motor.Tick(Frame, SwipeCommand.None);
            }

            // ...then fall back down to it. Now we are descending, ~3 frames from touchdown — well
            // inside the 9-frame (0.15s) buffer window.
            while (_go.transform.localPosition.y > NearGround)
            {
                _motor.Tick(Frame, SwipeCommand.None);
            }

            Assert.That(_motor.IsGrounded, Is.False, "Precondition: still airborne.");

            // The early swipe.
            _motor.Tick(Frame, SwipeCommand.Up);

            // Land.
            var frames = 0;
            while (!_motor.IsGrounded && frames < 30)
            {
                frames++;
                _motor.Tick(Frame, SwipeCommand.None);
            }

            Assert.That(_motor.IsGrounded, Is.True, "Precondition: landed.");

            // The next frame is the first on which a jump is legal, so this is when the buffered
            // input must be honoured.
            _motor.Tick(Frame, SwipeCommand.None);

            Assert.That(_motor.IsGrounded, Is.False,
                "A jump swiped a few frames before landing must fire on touchdown — that is the " +
                "entire point of the buffer.");
        }

        [Test]
        public void SwipeDownWhileAirborne_FastFallsAndLandsSliding()
        {
            _motor.Tick(Frame, SwipeCommand.Up);
            Idle(3);

            var heightBefore = _go.transform.localPosition.y;

            _motor.Tick(Frame, SwipeCommand.Down);
            _motor.Tick(Frame, SwipeCommand.None);

            Assert.That(_go.transform.localPosition.y, Is.LessThan(heightBefore),
                "Swipe-down in the air must drive the player down, not leave them floating.");

            // Fall to ground.
            var frames = 0;
            while (!_motor.IsGrounded && frames < 60)
            {
                frames++;
                _motor.Tick(Frame, SwipeCommand.None);
            }

            _motor.Tick(Frame, SwipeCommand.None);

            Assert.That(_motor.IsSliding, Is.True,
                "The buffered slide must fire on landing: the player asked to slide, and they " +
                "should land already sliding.");
        }

        // -------------------------------------------------------------------------------
        // Slide
        // -------------------------------------------------------------------------------

        [Test]
        public void SlideShrinksTheHitboxAndRestoresIt()
        {
            Assert.That(_motor.CurrentHeight, Is.EqualTo(StandingHeight).Within(0.001f));

            _motor.Tick(Frame, SwipeCommand.Down);

            Assert.That(_motor.IsSliding, Is.True);
            Assert.That(_motor.CurrentHeight,
                Is.EqualTo(StandingHeight * _tuning.SlideHeightFactor).Within(0.001f),
                "The slide must actually lower the hitbox, or the player clips the barrier they " +
                "just ducked under.");

            // SlideDuration is 0.55s ≈ 33 frames.
            Idle(40);

            Assert.That(_motor.IsSliding, Is.False);
            Assert.That(_motor.CurrentHeight, Is.EqualTo(StandingHeight).Within(0.001f));
        }

        [Test]
        public void SlideHitboxStaysOnTheGround()
        {
            // Shrinking the box about its centre would lift the bottom edge off the floor, and the
            // player would slide straight through a ground-level obstacle.
            _motor.Tick(Frame, SwipeCommand.Down);

            var bounds = _motor.Bounds;

            Assert.That(bounds.min.y, Is.EqualTo(0f).Within(0.001f),
                "The slide hitbox must stay anchored to the ground.");
        }

        [Test]
        public void JumpingCancelsASlide()
        {
            _motor.Tick(Frame, SwipeCommand.Down);
            Assert.That(_motor.IsSliding, Is.True);

            _motor.Tick(Frame, SwipeCommand.Up);

            Assert.That(_motor.IsSliding, Is.False);
            Assert.That(_motor.IsGrounded, Is.False);
        }

        // -------------------------------------------------------------------------------
        // Lean (presentation only — must never affect the hitbox)
        // -------------------------------------------------------------------------------

        [Test]
        public void LeaningNeverChangesTheHitbox()
        {
            // The lean is pure visual flourish. If it ever leaked into the collision box, a player
            // would be killed by an obstacle they visibly did not touch — the single most enraging
            // bug a runner can have.
            var standing = _motor.Bounds;

            _motor.Tick(Frame, SwipeCommand.Right);
            Idle(3); // mid-lane-change: this is when the lean is at its strongest

            var leaning = _motor.Bounds;

            Assert.That(leaning.size, Is.EqualTo(standing.size),
                "The hitbox size must be identical whether or not the player is leaning.");
            Assert.That(leaning.min.y, Is.EqualTo(standing.min.y).Within(0.001f),
                "Leaning must not lift or lower the hitbox.");
        }

        [Test]
        public void ResetClearsEverything()
        {
            _motor.Tick(Frame, SwipeCommand.Right);
            _motor.Tick(Frame, SwipeCommand.Up);

            _motor.Reset();

            Assert.That(_motor.CurrentLane, Is.EqualTo(Lane.Centre));
            Assert.That(_motor.IsGrounded, Is.True);
            Assert.That(_motor.IsSliding, Is.False);
            Assert.That(_go.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(_go.transform.localRotation, Is.EqualTo(Quaternion.identity));
        }

        [Test]
        public void StaleBufferedInput_DoesNotSurviveAReset()
        {
            // A buffered jump from the run you just died in must not fire on the first frame of the
            // next one.
            _motor.Tick(Frame, SwipeCommand.Up);
            _motor.Tick(Frame, SwipeCommand.Up); // buffered while airborne

            _motor.Reset();
            _motor.Tick(Frame, SwipeCommand.None);

            Assert.That(_motor.IsGrounded, Is.True,
                "A buffered input must not leak across runs.");
        }
    }
}
