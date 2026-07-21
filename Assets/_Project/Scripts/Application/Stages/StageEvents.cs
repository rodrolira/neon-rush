namespace NeonRush.Application.Stages
{
    // Readonly structs, published on the bus. Consumed by the menu (to refresh), analytics, and audio.

    /// <summary>A stage's objectives were all completed and its reward paid. The next stage is now current.</summary>
    public readonly struct StageCompleted
    {
        /// <summary>The stage number that was just cleared.</summary>
        public readonly int Number;

        /// <summary>Reward actually paid — already scaled by the prestige loop.</summary>
        public readonly int RewardCoins;
        public readonly int RewardGems;

        /// <summary>Prestige level this stage was cleared at (0 on the first pass through the ladder).</summary>
        public readonly int Prestige;

        public StageCompleted(int number, int rewardCoins, int rewardGems, int prestige)
        {
            Number = number;
            RewardCoins = rewardCoins;
            RewardGems = rewardGems;
            Prestige = prestige;
        }
    }

    /// <summary>Progress moved on the current stage. The menu listens to keep its objective lines live.</summary>
    public readonly struct StageProgressed
    {
        public readonly int Number;

        public StageProgressed(int number) => Number = number;
    }
}
