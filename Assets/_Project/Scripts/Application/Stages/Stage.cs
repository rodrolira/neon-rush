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
            new Stage(1, "First Steps", new[]
            {
                new StageObjective(MissionMetric.CompleteRuns, 3, "Finish 3 runs"),
                new StageObjective(MissionMetric.CollectCoins, 100, "Collect 100 coins"),
                new StageObjective(MissionMetric.TravelDistance, 1_000, "Travel 1,000 m"),
            }, rewardCoins: 500, rewardGems: 0),

            new Stage(2, "Neon Alleys", new[]
            {
                new StageObjective(MissionMetric.CollectCoins, 300, "Collect 300 coins"),
                new StageObjective(MissionMetric.Jump, 30, "Jump 30 times"),
                new StageObjective(MissionMetric.TravelDistance, 3_000, "Travel 3,000 m"),
            }, rewardCoins: 1_000, rewardGems: 5),

            new Stage(3, "Rush Hour", new[]
            {
                new StageObjective(MissionMetric.SingleRunDistance, 800, "Run 800 m in one go"),
                new StageObjective(MissionMetric.Slide, 20, "Slide 20 times"),
                new StageObjective(MissionMetric.CollectCoins, 600, "Collect 600 coins"),
            }, rewardCoins: 1_500, rewardGems: 5),

            new Stage(4, "Overdrive", new[]
            {
                new StageObjective(MissionMetric.SingleRunDistance, 1_200, "Run 1,200 m in one go"),
                new StageObjective(MissionMetric.CompleteRuns, 10, "Finish 10 runs"),
                new StageObjective(MissionMetric.TravelDistance, 8_000, "Travel 8,000 m"),
            }, rewardCoins: 2_500, rewardGems: 10),

            new Stage(5, "Neon Legend", new[]
            {
                new StageObjective(MissionMetric.CollectCoins, 2_000, "Collect 2,000 coins"),
                new StageObjective(MissionMetric.Jump, 100, "Jump 100 times"),
                new StageObjective(MissionMetric.TravelDistance, 15_000, "Travel 15,000 m"),
            }, rewardCoins: 4_000, rewardGems: 15),
        };
    }
}
