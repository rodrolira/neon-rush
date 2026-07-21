using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Missions;
using NeonRush.Application.Stages;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the stage campaign: objectives advance from gameplay events, a stage pays and
    /// advances only when ALL of its objectives are done, the ladder loops into prestige with scaled
    /// rewards, and progress restores from a save.
    /// </summary>
    [TestFixture]
    public sealed class StageTests
    {
        private EventBus _bus;
        private Wallet _wallet;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _wallet = new Wallet(_bus, 0, 0);
        }

        [TearDown]
        public void TearDown() => _bus.Dispose();

        private static IReadOnlyList<Stage> OneStage(params StageObjective[] objectives) =>
            new[] { new Stage(1, "Test", objectives, rewardCoins: 100, rewardGems: 5) };

        private StageService Build(IReadOnlyList<Stage> ladder) => new(_wallet, _bus, ladder);

        private void RunEnded(int distance) =>
            _bus.Publish(new RunEnded(1, distance, 0, 0, DeathCause.HitObstacle, 10f));

        [Test]
        public void Objectives_AdvanceFromEvents()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CollectCoins, 100, "coins")));

            _bus.Publish(new CoinCollected(40, 40));

            Assert.That(stages.ProgressAt(0), Is.EqualTo(40));
        }

        [Test]
        public void CompletingEveryObjective_PaysTheReward_AndLoopsToPrestige()
        {
            using var stages = Build(OneStage(
                new StageObjective(MissionMetric.CollectCoins, 100, "coins"),
                new StageObjective(MissionMetric.CompleteRuns, 2, "runs")));

            var completed = new List<StageCompleted>();
            using var _ = _bus.Subscribe<StageCompleted>(e => completed.Add(e));

            _bus.Publish(new CoinCollected(100, 100)); // first objective done, stage not yet complete
            Assert.That(completed, Is.Empty, "A stage must not clear until every objective is done.");

            RunEnded(300);
            RunEnded(300); // second objective (2 runs) done -> stage clears

            Assert.That(completed, Has.Count.EqualTo(1));
            Assert.That(completed[0].Number, Is.EqualTo(1));
            Assert.That(completed[0].Prestige, Is.EqualTo(0), "The first clear happens at prestige 0.");
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(100));
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(5));

            // Clearing the only stage loops back to stage one at the next prestige — never a dead end.
            Assert.That(stages.Prestige, Is.EqualTo(1));
            Assert.That(stages.CurrentStageNumber, Is.EqualTo(1));
            Assert.That(stages.ProgressAt(0), Is.EqualTo(0), "The looped stage starts fresh.");
        }

        [Test]
        public void Rewards_ScaleWithPrestige()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CompleteRuns, 1, "run")));
            stages.Restore(1, prestige: 1, savedProgress: null); // already one loop deep

            RunEnded(100); // clears the stage at prestige 1

            // Prestige 1 pays double (multiplier = 1 + prestige).
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(200));
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(10));
            Assert.That(stages.Prestige, Is.EqualTo(2));
        }

        [Test]
        public void SingleRunDistance_IsAHighWaterMark_NotASum()
        {
            using var stages = Build(OneStage(
                new StageObjective(MissionMetric.SingleRunDistance, 800, "sprint"),
                new StageObjective(MissionMetric.Jump, 999, "jumps"))); // keep the stage open

            RunEnded(500);
            Assert.That(stages.ProgressAt(0), Is.EqualTo(500));

            RunEnded(300); // a worse run must not add, nor reduce
            Assert.That(stages.ProgressAt(0), Is.EqualTo(500));

            RunEnded(900); // a better run reaches the target (capped)
            Assert.That(stages.ProgressAt(0), Is.EqualTo(800));
        }

        [Test]
        public void Progress_IsCappedAtTheTarget()
        {
            using var stages = Build(OneStage(
                new StageObjective(MissionMetric.CollectCoins, 100, "coins"),
                new StageObjective(MissionMetric.Jump, 999, "jumps"))); // keep the stage open

            _bus.Publish(new CoinCollected(500, 500));

            Assert.That(stages.ProgressAt(0), Is.EqualTo(100));
        }

        [Test]
        public void LoopingIsInfinite_AndNeverCrashes()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CompleteRuns, 1, "run")));

            for (var i = 0; i < 5; i++) RunEnded(100); // clear it five times over

            Assert.That(stages.Prestige, Is.EqualTo(5));
            Assert.That(stages.CurrentStageNumber, Is.EqualTo(1));
        }

        [Test]
        public void Restore_SetsTheStagePrestigeAndProgress()
        {
            var ladder = new[]
            {
                new Stage(1, "One", new[] { new StageObjective(MissionMetric.CompleteRuns, 3, "runs") }, 100, 0),
                new Stage(2, "Two", new[] { new StageObjective(MissionMetric.CollectCoins, 200, "coins") }, 200, 0),
            };

            using var stages = new StageService(_wallet, _bus, ladder);
            stages.Restore(2, prestige: 3, savedProgress: new[] { 120 });

            Assert.That(stages.CurrentStageNumber, Is.EqualTo(2));
            Assert.That(stages.Prestige, Is.EqualTo(3));
            Assert.That(stages.ProgressAt(0), Is.EqualTo(120));
        }

        [Test]
        public void Restore_BeyondTheLadder_ClampsIntoRange()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CompleteRuns, 1, "run")));

            stages.Restore(99, prestige: 0, savedProgress: null);

            Assert.That(stages.CurrentStageNumber, Is.EqualTo(1));
        }
    }
}
