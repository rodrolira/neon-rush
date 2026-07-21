using System;
using System.Collections.Generic;
using NeonRush.Application.Missions;

namespace NeonRush.Application.Stages
{
    /// <summary>
    /// One objective inside a stage: a metric to push and a target to hit. Reuses
    /// <see cref="MissionMetric"/> — the game already publishes every event these track, so a stage
    /// objective needs no new gameplay wiring, exactly like a daily mission. It carries no reward of
    /// its own: the reward belongs to the <see cref="Stage"/>, paid only when every objective is done.
    /// </summary>
    public readonly struct StageObjective
    {
        public readonly MissionMetric Metric;
        public readonly int Target;
        public readonly string Description;

        public StageObjective(MissionMetric metric, int target, string description)
        {
            if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target));

            Metric = metric;
            Target = target;
            Description = description ?? metric.ToString();
        }

        /// <summary>
        /// True for metrics measured as a best-single-value rather than a running sum — only
        /// <see cref="MissionMetric.SingleRunDistance"/> so far. High-water objectives take the max of
        /// each run instead of adding it, so a great run counts once, not forever.
        /// </summary>
        public bool IsHighWater => Metric == MissionMetric.SingleRunDistance;
    }

    /// <summary>
    /// A numbered stage in the campaign: a themed name, a handful of objectives that must ALL be
    /// completed to clear it, and the reward for clearing it. Immutable content.
    /// </summary>
    public sealed class Stage
    {
        public Stage(int number, string name, IReadOnlyList<StageObjective> objectives, int rewardCoins, int rewardGems)
        {
            if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
            if (objectives == null || objectives.Count == 0) throw new ArgumentException("A stage needs objectives.", nameof(objectives));
            if (rewardCoins < 0) throw new ArgumentOutOfRangeException(nameof(rewardCoins));
            if (rewardGems < 0) throw new ArgumentOutOfRangeException(nameof(rewardGems));

            Number = number;
            Name = name ?? $"Stage {number}";
            Objectives = objectives;
            RewardCoins = rewardCoins;
            RewardGems = rewardGems;
        }

        public int Number { get; }
        public string Name { get; }
        public IReadOnlyList<StageObjective> Objectives { get; }
        public int RewardCoins { get; }
        public int RewardGems { get; }
    }

    /// <summary>
    /// The shipped campaign: a fixed ladder of escalating stages. Compiled content, like the battle
    /// pass's default season — Remote Config can extend or retune it later, but a fresh offline install
    /// gets a complete progression from stage one.
    ///
    /// The curve is deliberate: stage one is clearable in a couple of short runs so a new player feels
    /// the system reward them immediately; later stages need real distance and skill (long single
    /// runs), so the campaign keeps pulling the player back for weeks, not minutes.
    /// </summary>
    public static class StageLadder
    {
        public static IReadOnlyList<Stage> Default() => new[]
        {
            // Stage 1 is a two-minute tutorial-by-doing: one run, a handful of coins, a short distance.
            // A new player clears it in their first session and feels the campaign reward them at once.
            new Stage(1, "First Steps", new[]
            {
                new StageObjective(MissionMetric.CompleteRuns, 1, "Finish 1 run"),
                new StageObjective(MissionMetric.CollectCoins, 40, "Collect 40 coins"),
                new StageObjective(MissionMetric.TravelDistance, 400, "Travel 400 m"),
            }, rewardCoins: 300, rewardGems: 0),

            new Stage(2, "Warming Up", new[]
            {
                new StageObjective(MissionMetric.CompleteRuns, 3, "Finish 3 runs"),
                new StageObjective(MissionMetric.CollectCoins, 150, "Collect 150 coins"),
                new StageObjective(MissionMetric.TravelDistance, 1_500, "Travel 1,500 m"),
            }, rewardCoins: 500, rewardGems: 0),

            new Stage(3, "Neon Alleys", new[]
            {
                new StageObjective(MissionMetric.CollectCoins, 300, "Collect 300 coins"),
                new StageObjective(MissionMetric.Jump, 30, "Jump 30 times"),
                new StageObjective(MissionMetric.TravelDistance, 3_000, "Travel 3,000 m"),
            }, rewardCoins: 900, rewardGems: 5),

            new Stage(4, "Rush Hour", new[]
            {
                new StageObjective(MissionMetric.SingleRunDistance, 800, "Run 800 m in one go"),
                new StageObjective(MissionMetric.Slide, 25, "Slide 25 times"),
                new StageObjective(MissionMetric.CollectCoins, 600, "Collect 600 coins"),
            }, rewardCoins: 1_300, rewardGems: 5),

            new Stage(5, "Overdrive", new[]
            {
                new StageObjective(MissionMetric.SingleRunDistance, 1_200, "Run 1,200 m in one go"),
                new StageObjective(MissionMetric.CompleteRuns, 12, "Finish 12 runs"),
                new StageObjective(MissionMetric.TravelDistance, 8_000, "Travel 8,000 m"),
            }, rewardCoins: 2_000, rewardGems: 10),

            new Stage(6, "Gridlock", new[]
            {
                new StageObjective(MissionMetric.CollectCoins, 1_200, "Collect 1,200 coins"),
                new StageObjective(MissionMetric.Jump, 80, "Jump 80 times"),
                new StageObjective(MissionMetric.Slide, 60, "Slide 60 times"),
            }, rewardCoins: 2_800, rewardGems: 10),

            new Stage(7, "Afterburner", new[]
            {
                new StageObjective(MissionMetric.SingleRunDistance, 1_800, "Run 1,800 m in one go"),
                new StageObjective(MissionMetric.CompleteRuns, 20, "Finish 20 runs"),
                new StageObjective(MissionMetric.TravelDistance, 15_000, "Travel 15,000 m"),
            }, rewardCoins: 3_800, rewardGems: 15),

            new Stage(8, "Neon Legend", new[]
            {
                new StageObjective(MissionMetric.CollectCoins, 3_000, "Collect 3,000 coins"),
                new StageObjective(MissionMetric.SingleRunDistance, 2_500, "Run 2,500 m in one go"),
                new StageObjective(MissionMetric.TravelDistance, 25_000, "Travel 25,000 m"),
            }, rewardCoins: 5_000, rewardGems: 20),
        };
    }
}
