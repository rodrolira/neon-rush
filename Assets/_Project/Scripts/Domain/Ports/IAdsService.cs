using System;
using NeonRush.Domain.Ads;

namespace NeonRush.Domain.Ports
{
    /// <summary>
    /// The ad network, behind a door.
    ///
    /// Nothing in the game may talk to AdMob directly. Everything goes through this port, which buys
    /// three concrete things:
    ///
    ///  · The whole ad *policy* — when an ad may be shown, how often, to whom — is testable without
    ///    an SDK, a network, or a device. That policy is where the revenue and the churn actually
    ///    live, and it is the part most likely to be got wrong.
    ///  · Ad removal for subscribers is a swapped implementation, not a nest of if-statements
    ///    threaded through the game.
    ///  · Replacing or A/B-testing the mediation layer is a one-file change.
    /// </summary>
    public interface IAdsService
    {
        /// <summary>True when a rewarded ad is loaded and can be shown *right now*.</summary>
        /// <remarks>
        /// Offering a reward and then failing to deliver an ad is worse than never offering it. The
        /// UI must consult this before showing the button, never after the player has tapped it.
        /// </remarks>
        bool IsRewardedReady { get; }

        /// <summary>True when an interstitial is loaded.</summary>
        bool IsInterstitialReady { get; }

        /// <summary>
        /// Starts loading the next ads. Cheap to call; implementations must ignore it if a load is
        /// already in flight.
        /// </summary>
        /// <remarks>
        /// Call this early and often — at boot, and immediately after every ad is consumed. An ad
        /// that is not preloaded is an ad that is not ready when the player asks for it, and a
        /// "Revive?" button that spins for four seconds is a button nobody presses twice.
        /// </remarks>
        void Preload();

        /// <summary>
        /// Shows a rewarded ad. <paramref name="onFinished"/> is invoked exactly once.
        ///
        /// The callback carries the outcome, and the caller MUST grant the reward only on
        /// <see cref="AdResult.Completed"/>. Granting on any other result — or on show, or on
        /// dismiss — is how a game leaks currency to anyone who taps and immediately closes.
        /// </summary>
        void ShowRewarded(AdPlacement placement, Action<AdResult> onFinished);

        /// <summary>
        /// Shows an interstitial. Never call this without asking <see cref="Ads.AdPolicy"/> first.
        /// </summary>
        void ShowInterstitial(AdPlacement placement, Action<AdResult> onFinished);

        /// <summary>
        /// Turns off interstitials permanently for this player (subscription, or a no-ads IAP).
        ///
        /// Rewarded ads are deliberately NOT disabled by this. A player who paid to remove ads has
        /// bought freedom from *interruption*, not from opportunity — and taking away their ability
        /// to double their coins would be removing a feature they still want. Ad-removal purchasers
        /// remain among the most engaged rewarded-ad viewers in every runner that ships this way.
        /// </summary>
        void DisableInterstitials();
    }
}
