namespace NeonRush.Domain.Economy
{
    /// <summary>
    /// The two currencies. There are deliberately only two, and they are deliberately not
    /// interchangeable in both directions.
    /// </summary>
    public enum CurrencyType
    {
        /// <summary>
        /// Soft currency. Earned by playing — coins picked up during a run.
        ///
        /// Coins buy progression: upgrades, common cosmetics, continues. They are abundant by
        /// design. A soft currency the player never has enough of stops feeling like a reward and
        /// starts feeling like a paywall, and players who feel walled do not convert — they leave.
        /// </summary>
        Coins = 0,

        /// <summary>
        /// Premium currency. Bought with money, or granted sparingly (daily rewards, missions,
        /// season pass).
        ///
        /// Gems buy time and exclusivity: rare skins, instant unlocks, season pass. The key
        /// economic rule is that gems flow *down* into coins and never back up — a player can spend
        /// gems for coins, but no amount of play converts coins into gems. The moment coins can buy
        /// gems, the real-money price of a gem is capped by how long a grinder is willing to play,
        /// and the premium economy collapses.
        /// </summary>
        Gems = 1,
    }

    /// <summary>
    /// Why a balance changed. This is not decoration — it is the backbone of the economy dashboard.
    ///
    /// Every currency movement in the game carries one of these, so that "where do gems come from
    /// and where do they go?" is a query rather than an archaeology project. Without a reason on
    /// every transaction, an inflating economy is undiagnosable: you can see the balance rising and
    /// have no idea which faucet is open.
    /// </summary>
    public enum TransactionReason
    {
        Unknown = 0,

        // --- Faucets (currency entering the economy) ---
        RunReward = 1,
        RunRewardDoubled = 2,   // rewarded-ad multiplier
        DailyReward = 3,
        MissionReward = 4,
        SeasonPassReward = 5,
        AchievementReward = 6,
        IapPurchase = 7,        // real money
        SubscriptionGrant = 8,
        GemToCoinExchange = 9,
        Refund = 10,
        AdminGrant = 11,        // support / compensation

        // --- Sinks (currency leaving the economy) ---
        StorePurchase = 20,
        Continue = 21,          // pay to revive
        UpgradePowerup = 22,
        SeasonPassUnlock = 23,
        GemToCoinExchangeCost = 24,
    }

    /// <summary>Classifies a reason as money entering or leaving the economy. Used by the analytics funnel.</summary>
    public static class TransactionReasonExtensions
    {
        /// <summary>True when this reason creates currency (a faucet), false when it destroys it (a sink).</summary>
        public static bool IsFaucet(this TransactionReason reason) => (int)reason < 20;

        /// <summary>True when this reason destroys currency (a sink).</summary>
        public static bool IsSink(this TransactionReason reason) => (int)reason >= 20;
    }
}
