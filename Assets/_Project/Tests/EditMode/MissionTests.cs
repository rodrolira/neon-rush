using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Missions;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    [TestFixture]
    public sealed class MissionServiceTests
    {
        private EventBus _bus;
        private Wallet _wallet;
        private FakeClock _clock;
        private MissionService _missions;

        /// <summary>A fixed pool so tests control exactly which missions are active.</summary>
        private static IReadOnlyList<MissionDefinition> TestPool() => new[]
        {
            new MissionDefinition("m_coins", "Collect 10 coins", MissionMetric.CollectCoins, 10, 100),
            new MissionDefinition("m_jump", "Jump 3 times", MissionMetric.Jump, 3, 100),
            new MissionDefinition("m_sprint", "Run 500 m in one run", MissionMetric.SingleRunDistance, 500, 200),
        };

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _wallet = new Wallet(_bus);
            _clock = new FakeClock { UtcNow = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc) };

            // Pool size == ActiveCount, so all three test missions are always the active set.
            _missions = new MissionService(_wallet, _clock, _bus, TestPool());
            _missions.RefreshIfNewDay();
        }

        [TearDown]
        public void TearDown()
        {
            _missions.Dispose();
            _bus.Dispose();
        }

        private MissionState Find(string id)
        {
            foreach (var mission in _missions.Active)
            {
                if (mission.Definition.Id == id) return mission;
            }

            return null;
        }

        [Test]
        public void EventsDriveProgress_WithoutAnyGameplayCodeKnowing()
        {
            _bus.Publish(new CoinCollected(4, 4));
            _bus.Publish(new PlayerJumped());

            Assert.That(Find("m_coins").Progress, Is.EqualTo(4));
            Assert.That(Find("m_jump").Progress, Is.EqualTo(1));
        }

        [Test]
        public void CompletionPaysExactlyOnce_AndPublishes()
        {
            MissionCompleted? completed = null;
            using var subscription = _bus.Subscribe<MissionCompleted>(e => completed = e);

            for (var i = 0; i < 5; i++) _bus.Publish(new PlayerJumped()); // target is 3

            Assert.That(completed.HasValue, Is.True);
            Assert.That(completed.Value.MissionId, Is.EqualTo("m_jump"));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(100),
                "The reward is paid once — extra jumps past the target must not keep paying.");

            Assert.That(Find("m_jump").Progress, Is.EqualTo(3), "Progress clamps at the target.");
        }

        [Test]
        public void HighWaterMetric_TracksBestRunNotSum()
        {
            // Two 300m runs must NOT complete a "run 500m in one run" mission.
            _bus.Publish(new RunEnded(1, 300f, 0, 0, DeathCause.HitObstacle, 30f));
            _bus.Publish(new RunEnded(2, 300f, 0, 0, DeathCause.HitObstacle, 30f));

            Assert.That(Find("m_sprint").IsComplete, Is.False,
                "300 + 300 is not 600 in one run; the metric is a high-water mark.");
            Assert.That(Find("m_sprint").Progress, Is.EqualTo(300));

            _bus.Publish(new RunEnded(3, 520f, 0, 0, DeathCause.HitObstacle, 45f));

            Assert.That(Find("m_sprint").IsComplete, Is.True);
        }

        [Test]
        public void TheRealGameLoop_DrivesMissions()
        {
            // End-to-end through RunSession rather than hand-published events.
            var session = new RunSession(new RunTuning(), _bus);
            session.Begin();

            for (var i = 0; i < 10; i++) session.CollectCoin();

            Assert.That(Find("m_coins").IsComplete, Is.True);
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(100),
                "Mission reward only — run coins are banked by RunRewardService, which is not in this fixture.");
        }

        [Test]
        public void SelectionIsDeterministicFromTheDay()
        {
            // Same day, two independent services (with the default pool this time): same missions.
            var a = new MissionService(_wallet, _clock, _bus);
            var b = new MissionService(_wallet, _clock, _bus);

            a.RefreshIfNewDay();
            b.RefreshIfNewDay();

            for (var i = 0; i < MissionService.ActiveCount; i++)
            {
                Assert.That(a.Active[i].Definition.Id, Is.EqualTo(b.Active[i].Definition.Id),
                    "Everyone gets the same missions on the same day.");
            }

            a.Dispose();
            b.Dispose();
        }

        [Test]
        public void ANewDay_RegeneratesAndDiscardsProgress()
        {
            _bus.Publish(new PlayerJumped());
            Assert.That(Find("m_jump").Progress, Is.EqualTo(1));

            _clock.UtcNow = _clock.UtcNow.AddDays(1);
            _missions.RefreshIfNewDay();

            Assert.That(Find("m_jump").Progress, Is.Zero, "Yesterday's progress does not carry into today.");
        }

        [Test]
        public void ARolledBackClock_NeverRerollsTheMissions()
        {
            // The classic mission exploit: reroll-until-easy by moving the device clock. Refreshing
            // only on "day advanced" makes it structurally impossible.
            _bus.Publish(new PlayerJumped());

            _clock.UtcNow = _clock.UtcNow.AddDays(-3);
            _missions.RefreshIfNewDay();

            Assert.That(Find("m_jump"), Is.Not.Null, "The active set survives a clock rollback.");
            Assert.That(Find("m_jump").Progress, Is.EqualTo(1), "And so does its progress.");
        }

        [Test]
        public void RestoreProgress_OnlyAppliesToTheSameDay()
        {
            var today = MissionService.DayStamp(_clock.UtcNow);

            _missions.RestoreProgress(today, new List<(string, int, bool)> { ("m_jump", 2, false) });
            Assert.That(Find("m_jump").Progress, Is.EqualTo(2), "Same-day progress is restored.");

            // A save from yesterday must not seed today's missions.
            _missions.RefreshIfNewDay();
            _clock.UtcNow = _clock.UtcNow.AddDays(1);
            _missions.RefreshIfNewDay();

            _missions.RestoreProgress(today, new List<(string, int, bool)> { ("m_jump", 2, false) });
            Assert.That(Find("m_jump").Progress, Is.Zero, "Stale-day progress is discarded.");
        }

        [Test]
        public void RestoredRewardedFlag_PreventsDoublePaying()
        {
            // The reload exploit: complete a mission, kill the app before the save, reload, complete
            // again. The Rewarded flag persisted with progress is what blocks the second payout.
            var today = MissionService.DayStamp(_clock.UtcNow);
            _missions.RestoreProgress(today, new List<(string, int, bool)> { ("m_jump", 3, true) });

            _bus.Publish(new PlayerJumped());

            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.Zero,
                "A mission restored as already-rewarded must never pay again.");
        }
    }
}
