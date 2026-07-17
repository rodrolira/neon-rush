namespace NeonRush.Domain.BattlePass
{
    // Battle-pass events. Readonly structs, published on the zero-alloc bus, consumed independently
    // by the UI (refresh the ladder), analytics (season engagement is the metric a pass lives on)
    // and audio. They live in Domain because BattlePassState — a Domain type — raises them, and the
    // Domain may not depend on any layer above it.

    /// <summary>Progress advanced. Fired on every XP gain, whether or not it crossed a tier.</summary>
    public readonly struct BattlePassProgressed
    {
        public readonly int Xp;
        public readonly int Tier;
        public readonly bool TierChanged;

        public BattlePassProgressed(int xp, int tier, bool tierChanged)
        {
            Xp = xp;
            Tier = tier;
            TierChanged = tierChanged;
        }
    }

    /// <summary>The premium pass was unlocked (bought). The premium track's rewards become claimable.</summary>
    public readonly struct BattlePassPremiumUnlocked
    {
        public readonly string SeasonId;

        public BattlePassPremiumUnlocked(string seasonId) => SeasonId = seasonId;
    }

    /// <summary>
    /// A tier reward was claimed. Carries enough for analytics and the wallet/inventory to act
    /// without re-reading the track: which rung, which track, and exactly what paid out.
    /// </summary>
    public readonly struct BattlePassRewardClaimed
    {
        public readonly int Level;
        public readonly bool Premium;
        public readonly BattlePassReward Reward;

        public BattlePassRewardClaimed(int level, bool premium, BattlePassReward reward)
        {
            Level = level;
            Premium = premium;
            Reward = reward;
        }
    }

    /// <summary>A new season began; progress was reset. Owned cosmetics are untouched.</summary>
    public readonly struct BattlePassSeasonStarted
    {
        public readonly string SeasonId;

        public BattlePassSeasonStarted(string seasonId) => SeasonId = seasonId;
    }
}
