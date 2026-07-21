namespace NeonRush.Application.Stages
{
    // Readonly structs, published on the bus. Consumed by the menu (to refresh), analytics, and audio.

    /// <summary>A stage's objectives were all completed and its reward paid. The next stage is now current.</summary>
    public readonly struct StageCompleted
    {
        /// <summary>The stage number that was just cleared.</summary>
        public readonly int Number;
        public readonly int RewardCoins;
        public readonly int RewardGems;

        public StageCompleted(int number, int rewardCoins, int rewardGems)
        {
            Number = number;
            RewardCoins = rewardCoins;
            RewardGems = rewardGems;
        }
    }

    /// <summary>Progress moved on the current stage. The menu listens to keep its objective lines live.</summary>
    public readonly struct StageProgressed
    {
        public readonly int Number;

        public StageProgressed(int number) => Number = number;
    }
}
