using System;

namespace NeonRush.Application.Subscription
{
    /// <summary>
    /// The tunable terms of the VIP subscription: how long a purchase lasts and what it is worth.
    /// Built and clamped from Remote Config in GameConfigService, like every other live number — a
    /// subscription's perks are exactly what LiveOps needs to tune against retention and ARPU without
    /// a store submission.
    ///
    /// The PRICE is not here: a subscription's price is owned by the platform store (App Store Connect
    /// / Play Console), because auto-renewing prices are regulated and must match what the store
    /// charges to the cent. This config governs only what the game grants for it.
    /// </summary>
    public sealed class SubscriptionConfig
    {
        /// <summary>Days added per purchase/renewal. A "monthly" pass in the shipping store maps here.</summary>
        public int DurationDays { get; set; } = 30;

        /// <summary>Gems granted once per day while subscribed. The daily hook that earns the recurring charge.</summary>
        public int DailyGems { get; set; } = 50;

        /// <summary>Multiplier applied to a run's coins while subscribed. 2 = double coins.</summary>
        public float CoinMultiplier { get; set; } = 2f;

        /// <summary>Master switch, so LiveOps can pull the whole offer instantly.</summary>
        public bool Enabled { get; set; } = true;

        public void Validate()
        {
            if (DurationDays <= 0) throw new ArgumentException($"{nameof(DurationDays)} must be > 0.");
            if (DailyGems < 0) throw new ArgumentException($"{nameof(DailyGems)} must be >= 0.");
            if (CoinMultiplier < 1f) throw new ArgumentException($"{nameof(CoinMultiplier)} must be >= 1.");
        }
    }
}
