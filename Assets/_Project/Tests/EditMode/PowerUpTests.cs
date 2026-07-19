using System.Collections.Generic;
using NeonRush.Application.PowerUps;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using NeonRush.Domain.PowerUps;
using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the power-up state machine — the pure timers and shield charges, with no scene.
    /// </summary>
    [TestFixture]
    public sealed class PowerUpStateTests
    {
        private PowerUpState _state;

        [SetUp]
        public void SetUp() => _state = new PowerUpState();

        [Test]
        public void ActivateMagnet_MakesItActive()
        {
            _state.ActivateMagnet(6f);

            Assert.That(_state.IsMagnetActive, Is.True);
            Assert.That(_state.MagnetRemaining, Is.EqualTo(6f).Within(0.0001f));
        }

        [Test]
        public void RefreshingATimedEffect_NeverShortensIt()
        {
            _state.ActivateMagnet(6f);
            _state.Tick(4f); // 2 s left

            _state.ActivateMagnet(5f); // a fresh 5 s pickup must win over the 2 s remaining

            Assert.That(_state.MagnetRemaining, Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void Tick_ExpiresATimedEffect()
        {
            _state.ActivateDoubleScore(3f);

            _state.Tick(3.5f);

            Assert.That(_state.IsDoubleScoreActive, Is.False);
            Assert.That(_state.DoubleScoreRemaining, Is.EqualTo(0f));
        }

        [Test]
        public void Shields_StackAndAreConsumedOneAtATime()
        {
            _state.AddShield();
            _state.AddShield();

            Assert.That(_state.ShieldCharges, Is.EqualTo(2));

            Assert.That(_state.TryConsumeShield(), Is.True);
            Assert.That(_state.ShieldCharges, Is.EqualTo(1));

            Assert.That(_state.TryConsumeShield(), Is.True);
            Assert.That(_state.TryConsumeShield(), Is.False, "A spent shield must not absorb a second hit.");
        }

        [Test]
        public void Shields_DoNotTickDown()
        {
            _state.AddShield();

            _state.Tick(1000f);

            Assert.That(_state.HasShield, Is.True, "A shield is a charge, not a timer — it waits for a hit.");
        }

        [Test]
        public void Reset_ClearsEverything()
        {
            _state.ActivateMagnet(6f);
            _state.ActivateDoubleScore(6f);
            _state.AddShield(3);

            _state.Reset();

            Assert.That(_state.IsMagnetActive, Is.False);
            Assert.That(_state.IsDoubleScoreActive, Is.False);
            Assert.That(_state.ShieldCharges, Is.EqualTo(0));
        }

        [Test]
        public void Activate_RejectsNonPositiveDuration()
        {
            Assert.That(() => _state.ActivateMagnet(0f), Throws.Exception);
            Assert.That(() => _state.ActivateDoubleScore(-1f), Throws.Exception);
        }
    }

    /// <summary>
    /// Tests for the power-up service — how a grabbed pickup becomes a running effect, drives the
    /// run's score multiplier, guards the fatal hit, and cleans up between runs.
    /// </summary>
    [TestFixture]
    public sealed class PowerUpServiceTests
    {
        private EventBus _bus;
        private RunSession _session;
        private PowerUpConfig _config;
        private PowerUpService _service;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _session = new RunSession(new RunTuning(), _bus);
            _config = new PowerUpConfig
            {
                Enabled = true,
                MagnetSeconds = 5f,
                DoubleScoreSeconds = 5f,
                ScoreMultiplier = 2f,
                ShieldChargesPerPickup = 1,
            };
            _service = new PowerUpService(_bus, _session, _config);
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
            _bus.Dispose();
        }

        [Test]
        public void CollectingAMagnet_ActivatesItAndAnnouncesIt()
        {
            var activations = new List<PowerUpActivated>();
            using var _ = _bus.Subscribe<PowerUpActivated>(e => activations.Add(e));

            _service.Collect(PowerUpType.Magnet);

            Assert.That(_service.IsMagnetActive, Is.True);
            Assert.That(activations, Has.Count.EqualTo(1));
            Assert.That(activations[0].Type, Is.EqualTo(PowerUpType.Magnet));
            Assert.That(activations[0].DurationSeconds, Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void CollectingAPickup_PublishesTheGrab()
        {
            var grabbed = new List<PowerUpType>();
            using var _ = _bus.Subscribe<PowerUpCollected>(e => grabbed.Add(e.Type));

            _service.Collect(PowerUpType.Shield);

            Assert.That(grabbed, Is.EqualTo(new[] { PowerUpType.Shield }));
        }

        [Test]
        public void DoubleScore_DrivesTheRunScoreMultiplier()
        {
            Assert.That(_session.ScoreMultiplier, Is.EqualTo(1f));

            _service.Collect(PowerUpType.DoubleScore);

            Assert.That(_session.ScoreMultiplier, Is.EqualTo(2f).Within(0.0001f));
        }

        [Test]
        public void DoubleScore_RestoresTheMultiplierAndAnnouncesExpiryWhenItRunsOut()
        {
            var expiries = new List<PowerUpType>();
            using var _ = _bus.Subscribe<PowerUpExpired>(e => expiries.Add(e.Type));

            _service.Collect(PowerUpType.DoubleScore);

            // One big delta that both spans and ends the effect — the hitch case.
            _service.Tick(6f);

            Assert.That(_service.IsDoubleScoreActive, Is.False);
            Assert.That(_session.ScoreMultiplier, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(expiries, Does.Contain(PowerUpType.DoubleScore));
        }

        [Test]
        public void Shield_BanksACharge_AndTryConsumeSpendsIt()
        {
            _service.Collect(PowerUpType.Shield);
            Assert.That(_service.ShieldCharges, Is.EqualTo(1));

            Assert.That(_service.TryConsumeShield(), Is.True);
            Assert.That(_service.ShieldCharges, Is.EqualTo(0));
            Assert.That(_service.TryConsumeShield(), Is.False);
        }

        [Test]
        public void ConsumingAShield_AnnouncesIt()
        {
            var consumed = new List<ShieldConsumed>();
            using var _ = _bus.Subscribe<ShieldConsumed>(e => consumed.Add(e));

            _service.Collect(PowerUpType.Shield);
            _service.TryConsumeShield();

            Assert.That(consumed, Has.Count.EqualTo(1));
            Assert.That(consumed[0].ChargesRemaining, Is.EqualTo(0));
        }

        [Test]
        public void StartingARun_ClearsEffectsFromTheLastOne()
        {
            _service.Collect(PowerUpType.Magnet);
            _service.Collect(PowerUpType.Shield);
            _service.Collect(PowerUpType.DoubleScore);

            _session.Begin(); // publishes RunStarted, which the service listens for

            Assert.That(_service.IsMagnetActive, Is.False);
            Assert.That(_service.ShieldCharges, Is.EqualTo(0));
            Assert.That(_service.IsDoubleScoreActive, Is.False);
            Assert.That(_session.ScoreMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void WhenDisabled_CollectDoesNothing()
        {
            _config.Enabled = false;
            using var service = new PowerUpService(_bus, _session, _config);

            service.Collect(PowerUpType.Magnet);
            service.Collect(PowerUpType.Shield);

            Assert.That(service.IsMagnetActive, Is.False);
            Assert.That(service.ShieldCharges, Is.EqualTo(0));
        }
    }

    /// <summary>
    /// Locks the score-multiplier contract on the run itself: the DoubleScore boost accelerates the
    /// distance score, and coin currency is never touched by it.
    /// </summary>
    [TestFixture]
    public sealed class ScoreMultiplierTests
    {
        [Test]
        public void ScoreMultiplier_AcceleratesTheDistanceScore()
        {
            var bus = new EventBus();
            var tuning = new RunTuning();

            var plain = new RunSession(tuning, bus);
            plain.Begin();
            for (var i = 0; i < 120; i++) plain.Tick(1f / 60f);

            var boosted = new RunSession(tuning, bus);
            boosted.Begin();
            boosted.ScoreMultiplier = 2f;
            for (var i = 0; i < 120; i++) boosted.Tick(1f / 60f);

            Assert.That(boosted.Score, Is.GreaterThan(plain.Score),
                "A 2x multiplier must make the distance score climb faster than the same run at 1x.");

            bus.Dispose();
        }

        [Test]
        public void Begin_ResetsAnyLeftoverMultiplier()
        {
            var bus = new EventBus();
            var session = new RunSession(new RunTuning(), bus);

            session.ScoreMultiplier = 5f;
            session.Begin();

            Assert.That(session.ScoreMultiplier, Is.EqualTo(1f),
                "A new run must never inherit the previous run's multiplier.");

            bus.Dispose();
        }
    }
}
