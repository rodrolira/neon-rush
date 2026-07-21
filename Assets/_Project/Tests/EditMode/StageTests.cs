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
    /// advances only when ALL of its objectives are done, and progress restores from a save.
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

        private void RunEnded(int distance) =>
            _bus.Publish(new RunEnded(1, distance, 0, 0, DeathCause.HitObstacle, 10f));

        // The ctor takes (wallet, bus, ladder); tests read cleaner with a local builder.
        private StageService Build(IReadOnlyList<Stage> ladder) => new(_wallet, _bus, ladder);

        [Test]
        public void Objectives_AdvanceFromEvents()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CollectCoins, 100, "coins")));

            _bus.Publish(new CoinCollected(40, 40));

            Assert.That(stages.ProgressAt(0), Is.EqualTo(40));
        }

        [Test]
        public void CompletingEveryObjective_PaysTheReward_AndAdvances()
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
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(100), "Stage reward coins must be credited.");
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(5), "Stage reward gems must be credited.");
            Assert.That(stages.IsAllComplete, Is.True, "Clearing the only stage completes the campaign.");
        }

        [Test]
        public void SingleRunDistance_IsAHighWaterMark_NotASum()
        {
            using var stages = Build(OneStage(
                new StageObjective(MissionMetric.SingleRunDistance, 800, "sprint")));

            RunEnded(500);
            Assert.That(stages.ProgressAt(0), Is.EqualTo(500));

            RunEnded(300); // a worse run must not add, nor reduce
            Assert.That(stages.ProgressAt(0), Is.EqualTo(500));

            RunEnded(900); // a better run clears it (capped at the target)
            Assert.That(stages.IsAllComplete, Is.True);
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
        public void AfterTheLastStage_NoFurtherAdvanceAndNoCrash()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CompleteRuns, 1, "run")));

            RunEnded(100); // clears the only stage
            Assert.That(stages.IsAllComplete, Is.True);

            // More events must be harmless once the campaign is done.
            Assert.DoesNotThrow(() =>
            {
                _bus.Publish(new CoinCollected(50, 50));
                RunEnded(500);
            });
        }

        [Test]
        public void Restore_SetsTheStageAndProgress()
        {
            var ladder = new[]
            {
                new Stage(1, "One", new[] { new StageObjective(MissionMetric.CompleteRuns, 3, "runs") }, 100, 0),
                new Stage(2, "Two", new[] { new StageObjective(MissionMetric.CollectCoins, 200, "coins") }, 200, 0),
            };

            using var stages = new StageService(_wallet, _bus, ladder);
            stages.Restore(2, new[] { 120 });

            Assert.That(stages.CurrentStageNumber, Is.EqualTo(2));
            Assert.That(stages.ProgressAt(0), Is.EqualTo(120));
        }

        [Test]
        public void Restore_BeyondTheLadder_MarksTheCampaignComplete()
        {
            using var stages = Build(OneStage(new StageObjective(MissionMetric.CompleteRuns, 1, "run")));

            stages.Restore(99, null);

            Assert.That(stages.IsAllComplete, Is.True);
        }
    }
}
