using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// The tests that guarantee each obstacle kind is beatable by exactly one move — and, above all,
    /// that the low block <b>can actually be jumped</b>. That was the shipped bug: every obstacle was
    /// a full-height wall taller than the jump apex, so the jump did nothing. These assertions make
    /// that regression impossible to reintroduce silently: if someone retunes the jump, the gravity,
    /// or an archetype's height so that a "jump" obstacle stops being jumpable, a test goes red.
    ///
    /// The numbers are derived the same way the collision system computes them at runtime, so the
    /// test is checking the real in-game outcome, not a parallel model:
    ///
    ///  · Player jump apex (feet) = JumpVelocity² / (2·Gravity)   — projectile motion.
    ///  · Player standing box: 0 .. StandingHeight.
    ///  · Player slide box:    0 .. StandingHeight · SlideHeightFactor.
    ///  · Obstacle hitbox (Y): CentreY ± Height · 0.9 / 2          — the 0.9 shrink is CollisionSystem's.
    ///
    /// A move "clears" an obstacle when the player's box does not overlap the obstacle's hitbox in Y
    /// at the relevant moment (apex for a jump, ground for a slide/standing).
    /// </summary>
    [TestFixture]
    public sealed class ObstacleArchetypeTests
    {
        /// <summary>
        /// The player's standing box height in metres. Fixed by the art contract and passed to
        /// PlayerMotor as <c>standingHeight</c> from GameBootstrap; mirrored here so the clearance
        /// maths matches what the game actually does.
        /// </summary>
        private const float StandingHeight = 1.6f;

        /// <summary>The Y hitbox shrink CollisionSystem applies to every obstacle (see HitObstacle).</summary>
        private const float HitboxYFactor = 0.9f;

        private RunTuning _tuning;
        private float _jumpApexFeet;
        private float _slideBoxTop;
        private float _standingBoxTop;

        [SetUp]
        public void SetUp()
        {
            _tuning = new RunTuning();

            _jumpApexFeet = _tuning.JumpVelocity * _tuning.JumpVelocity / (2f * _tuning.Gravity);
            _slideBoxTop = StandingHeight * _tuning.SlideHeightFactor;
            _standingBoxTop = StandingHeight;
        }

        private static float HitboxBottom(ObstacleArchetype a) => a.CentreY - a.Height * HitboxYFactor * 0.5f;
        private static float HitboxTop(ObstacleArchetype a) => a.CentreY + a.Height * HitboxYFactor * 0.5f;

        [Test]
        public void LowJump_IsClearedByAJump()
        {
            var low = ObstacleArchetype.For(ObstacleKind.LowJump);

            // At the apex the player's feet (the bottom of their box) are above the obstacle's top,
            // so the box is entirely above it: cleared.
            Assert.That(_jumpApexFeet, Is.GreaterThan(HitboxTop(low)),
                "The jump apex must lift the player's box clear over a low block — this is the bug this whole feature fixes.");
        }

        [Test]
        public void LowJump_IsNotClearedByASlide()
        {
            var low = ObstacleArchetype.For(ObstacleKind.LowJump);

            // The slide box hugs the ground, and so does the low block. Their Y ranges overlap, so a
            // slide runs straight into it — the low block reads as "jump me", not "slide me".
            Assert.That(_slideBoxTop, Is.GreaterThan(HitboxBottom(low)),
                "A slide must NOT save the player from a low block, or the jump loses its distinct purpose.");
        }

        [Test]
        public void HighSlide_IsClearedByASlide()
        {
            var high = ObstacleArchetype.For(ObstacleKind.HighSlide);

            // The slide box top sits below the hanging barrier's bottom: the player passes under it.
            Assert.That(_slideBoxTop, Is.LessThan(HitboxBottom(high)),
                "A slide must duck the player under the overhead barrier.");
        }

        [Test]
        public void HighSlide_IsNotClearedByStandingOrJumping()
        {
            var high = ObstacleArchetype.For(ObstacleKind.HighSlide);

            // Running into it: the standing box reaches into the barrier.
            Assert.That(_standingBoxTop, Is.GreaterThan(HitboxBottom(high)),
                "Running upright must hit the overhead barrier.");

            // Jumping into it: even at the apex the player's box still overlaps — the box rises as a
            // whole, so its top climbs past the barrier's bottom, and its bottom (the feet) never
            // makes it above the barrier's top. Jumping an overhead barrier is a death, as intended.
            var boxBottomAtApex = _jumpApexFeet;
            var boxTopAtApex = _jumpApexFeet + StandingHeight;
            var overlapsAtApex = boxBottomAtApex < HitboxTop(high) && boxTopAtApex > HitboxBottom(high);

            Assert.That(overlapsAtApex, Is.True,
                "Jumping into an overhead barrier must still collide — slide is the only escape.");
        }

        [Test]
        public void FullBlock_IsClearedByNeitherJumpNorSlide()
        {
            var wall = ObstacleArchetype.For(ObstacleKind.FullBlock);

            // Jump: the apex leaves the player's feet still below the wall's top, so the box overlaps.
            Assert.That(_jumpApexFeet, Is.LessThan(HitboxTop(wall)),
                "The full wall must be too tall to clear with a jump.");

            // Slide: the wall reaches the floor, so the slide box overlaps it.
            Assert.That(_slideBoxTop, Is.GreaterThan(HitboxBottom(wall)),
                "The full wall must reach the ground so a slide cannot pass under it.");
        }

        [Test]
        public void GroundedKinds_SitOnTheFloor()
        {
            // A grounded obstacle whose box floated above 0 would let the player slide under a block
            // that is supposed to be un-slideable. Assert both grounded kinds actually touch the floor.
            Assert.That(ObstacleArchetype.For(ObstacleKind.LowJump).Bottom, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(ObstacleArchetype.For(ObstacleKind.FullBlock).Bottom, Is.EqualTo(0f).Within(0.0001f));
        }
    }
}
