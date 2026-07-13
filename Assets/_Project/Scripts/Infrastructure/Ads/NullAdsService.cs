using System;
using NeonRush.Domain.Ads;
using NeonRush.Domain.Ports;

namespace NeonRush.Infrastructure.Ads
{
    /// <summary>
    /// An ad service that shows no ads.
    ///
    /// This is a real, shipping implementation of the Null Object pattern — not a stub, not a
    /// placeholder, not something to be filled in later. It is what actually runs in three real
    /// situations:
    ///
    ///  · <b>In CI.</b> There is no AdMob SDK on the build agent and no network. The entire ad
    ///    *policy* — which is where the revenue and the churn live — is tested against this.
    ///  · <b>In the Editor.</b> Nobody wants to sit through a test ad every time they die while
    ///    tuning the difficulty curve.
    ///  · <b>Before the SDK is integrated.</b> The game is complete and shippable without it; adding
    ///    AdMob is swapping one binding in the composition root.
    ///
    /// It reports rewarded ads as unavailable rather than pretending they completed. Faking a
    /// completion would silently grant currency in every dev build and mask the exact bug — paying
    /// out on the wrong callback — that the ad code most needs to be tested against.
    /// </summary>
    public sealed class NullAdsService : IAdsService
    {
        public bool IsRewardedReady => false;

        public bool IsInterstitialReady => false;

        public void Preload()
        {
            // Nothing to load.
        }

        public void ShowRewarded(AdPlacement placement, Action<AdResult> onFinished)
        {
            // NotAvailable, never Completed. The UI must handle "no ad" gracefully, and the only way
            // to be sure it does is to exercise that path constantly during development.
            onFinished?.Invoke(AdResult.NotAvailable);
        }

        public void ShowInterstitial(AdPlacement placement, Action<AdResult> onFinished)
        {
            onFinished?.Invoke(AdResult.NotAvailable);
        }

        public void DisableInterstitials()
        {
            // Already disabled, permanently.
        }
    }
}
