namespace NeonRush.Domain.Ads
{
    /// <summary>
    /// Where an ad is shown. Every placement is reported to analytics under this name, so that
    /// revenue and churn can be attributed to a specific moment in the game rather than to "ads" as
    /// an undifferentiated blob.
    ///
    /// The distinction that matters commercially: rewarded placements are *chosen* by the player and
    /// interstitials are *inflicted* on them. They are not the same product and must never be tuned
    /// with the same knobs.
    /// </summary>
    public enum AdPlacement
    {
        /// <summary>Rewarded. The player died and pays an ad to continue the same run.</summary>
        Revive = 0,

        /// <summary>Rewarded. The player doubles the coins they just earned.</summary>
        DoubleCoins = 1,

        /// <summary>Rewarded. Player-initiated, from the store or the daily reward screen.</summary>
        FreeCoins = 2,

        /// <summary>
        /// Interstitial. Shown between runs — never during one.
        ///
        /// This is the only placement the player did not ask for, and therefore the only one that
        /// can drive them away. Everything in <see cref="AdPolicy"/> exists to protect it from
        /// itself.
        /// </summary>
        BetweenRuns = 3,
    }

    /// <summary>Why a rewarded ad ended. Only <see cref="Completed"/> may grant the reward.</summary>
    public enum AdResult
    {
        /// <summary>The player watched it through. This is the ONLY value that earns a reward.</summary>
        Completed = 0,

        /// <summary>The player closed it early. No reward — they did not fulfil the bargain.</summary>
        Skipped = 1,

        /// <summary>No ad was available to show (no fill, no network).</summary>
        NotAvailable = 2,

        /// <summary>The ad SDK failed. Not the player's fault.</summary>
        Failed = 3,
    }
}
