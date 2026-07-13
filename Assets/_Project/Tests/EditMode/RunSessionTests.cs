using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the rules that decide what a run is worth and how hard it gets.
    ///
    /// None of this touches Unity. That is the payoff of keeping Application engine-free: the
    /// difficulty curve, the scoring and the milestone logic — the things most likely to be retuned
    /// and most expensive to get wrong — are verified in milliseconds, with no Editor, no device
    /// and no Unity licence in CI.
    /// </summary>
    [TestFixture]
    public sealed class RunSessionTests
    {
        private EventBus _bus;
        private RunTuning _tuning;
        private RunSession _session;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _tuning = new RunTuning();
            _session = new RunSession(_tuning, _bus);
        }

        [TearDown]
        public void TearDown() => _bus.Dispose();

        [Test]
        public void Begin_StartsAtBaseSpeedWithCleanState()
        {
            _session.Begin();

            Assert.That(_session.IsRunning, Is.True);
            Assert.That(_session.RunNumber, Is.EqualTo(1));
            Assert.That(_session.Distance, Is.Zero);
            Assert.That(_session.Coins, Is.Zero);
            Assert.That(_session.Score, Is.Zero);
            Assert.That(_session.Speed, Is.EqualTo(_tuning.BaseSpeed));
        }

        [Test]
        public void Tick_AccumulatesDistanceAtCurrentSpeed()
        {
            _session.Begin();

            _session.Tick(1f / 60f);

            var expected = _tuning.BaseSpeed * (1f / 60f);
            Assert.That(_session.Distance, Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void Speed_RampsWithDistanceAndClampsAtMax()
        {
            _session.Begin();

            Assert.That(_session.SpeedAt(0f), Is.EqualTo(_tuning.BaseSpeed).Within(0.001f));

            var mid = _session.SpeedAt(500f);
            Assert.That(mid, Is.GreaterThan(_tuning.BaseSpeed));
            Assert.That(mid, Is.LessThanOrEqualTo(_tuning.MaxSpeed));

            // The clamp is the safety rail that keeps the game reactable. Without it, speed grows
            // without bound and every death past a few kilometres is unavoidable.
            Assert.That(_session.SpeedAt(1_000_000f), Is.EqualTo(_tuning.MaxSpeed).Within(0.001f));
        }

        [Test]
        public void Score_IsNotLostToIntegerTruncation()
        {
            // The bug this guards: score accrues at ~0.15 per frame at 60 fps. Accumulating into an
            // int truncates every frame to 0, and the player's score sits at zero forever while they
            // sprint down the track. It is invisible in a quick playtest and catastrophic in review.
            _session.Begin();

            for (var i = 0; i < 60; i++)
            {
                _session.Tick(1f / 60f);
            }

            Assert.That(_session.Score, Is.GreaterThan(0),
                "Score must accumulate as a float internally; truncating each frame loses it entirely.");
        }

        [Test]
        public void Tick_ClampsAbsurdDeltaTime()
        {
            // A device that hitches, or an app resuming from the background, can hand Unity a delta
            // of several seconds. Without a clamp, the world scrolls tens of metres in one frame and
            // teleports the player through a wall of obstacles they never saw.
            _session.Begin();

            _session.Tick(5f);

            var maxPlausible = _tuning.MaxSpeed * 0.1f; // the clamp is 0.1s
            Assert.That(_session.Distance, Is.LessThanOrEqualTo(maxPlausible + 0.001f),
                "A huge frame delta must be clamped, or the player teleports through obstacles.");
        }

        [Test]
        public void CollectCoin_PublishesWithRunningTotal()
        {
            var received = new List<CoinCollected>();
            using var _ = _bus.Subscribe<CoinCollected>(e => received.Add(e));

            _session.Begin();
            _session.CollectCoin();
            _session.CollectCoin(5);

            Assert.That(_session.Coins, Is.EqualTo(6));
            Assert.That(received, Has.Count.EqualTo(2));
            Assert.That(received[1].Value, Is.EqualTo(5));
            Assert.That(received[1].TotalThisRun, Is.EqualTo(6));
        }

        [Test]
        public void End_IsIdempotent()
        {
            // A player can plausibly clip two obstacles in the same frame. Without the guard, that
            // fires RunEnded twice: coins are double-credited, the run is double-counted in
            // analytics, and the death screen opens on top of itself.
            var ended = 0;
            using var _ = _bus.Subscribe<RunEnded>(__ => ended++);

            _session.Begin();
            _session.End(DeathCause.HitObstacle);
            _session.End(DeathCause.HitObstacle);

            Assert.That(ended, Is.EqualTo(1), "RunEnded must fire exactly once per run.");
            Assert.That(_session.IsRunning, Is.False);
        }

        [Test]
        public void Tick_AfterEnd_DoesNothing()
        {
            _session.Begin();
            _session.Tick(0.1f);
            var distance = _session.Distance;

            _session.End(DeathCause.HitObstacle);
            _session.Tick(0.1f);

            Assert.That(_session.Distance, Is.EqualTo(distance), "A dead player must not keep running.");
        }

        [Test]
        public void Milestones_FireOncePerInterval_EvenAcrossALongFrame()
        {
            // At the speed cap a single long frame can cross more than one 100 m boundary. A naive
            // implementation that fires only the most recent milestone would silently under-count a
            // "run 10 km" mission.
            var milestones = new List<int>();
            using var _ = _bus.Subscribe<DistanceMilestone>(e => milestones.Add(e.Metres));

            _session.Begin();

            // Drive 350 m in small, realistic steps.
            for (var i = 0; i < 2000 && _session.Distance < 350f; i++)
            {
                _session.Tick(1f / 60f);
            }

            Assert.That(milestones, Is.EqualTo(new[] { 100, 200, 300 }),
                "Every 100 m boundary crossed must fire exactly once, in order.");
        }

        [Test]
        public void Begin_Twice_Throws()
        {
            _session.Begin();

            Assert.Throws<System.InvalidOperationException>(() => _session.Begin(),
                "Starting a run on top of a live one would silently discard the player's coins.");
        }

        [Test]
        public void InSafeStart_IsTrueOnlyForTheOpeningMetres()
        {
            _session.Begin();

            Assert.That(_session.InSafeStart, Is.True);

            while (_session.Distance < _tuning.SafeStartDistance + 1f)
            {
                _session.Tick(1f / 60f);
            }

            Assert.That(_session.InSafeStart, Is.False);
        }
    }
}
