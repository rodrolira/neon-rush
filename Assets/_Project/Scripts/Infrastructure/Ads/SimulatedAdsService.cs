using System;
using NeonRush.Domain.Ads;
using NeonRush.Domain.Ports;
using UnityEngine;

namespace NeonRush.Infrastructure.Ads
{
    /// <summary>
    /// An ad service that pretends. Development and QA only — never bound in a release build.
    ///
    /// It exists because the ad flows are the hardest part of the game to exercise by hand: to see
    /// what happens when a player watches a revive ad, you would otherwise need the SDK, a network,
    /// a real ad unit, and thirty seconds of your life per attempt. With this, the whole flow runs
    /// instantly in the Editor.
    ///
    /// Crucially, it can simulate <b>failure</b>, and that is most of its value. The bugs in ad code
    /// are never in the happy path — they are in "the player closed it after two seconds", "there was
    /// no fill", "the SDK called back twice". Those paths are exercised in production constantly and
    /// almost never during development, which is precisely why they ship broken.
    /// </summary>
    public sealed class SimulatedAdsService : IAdsService
    {
        private readonly AdResult _result;
        private bool _interstitialsDisabled;

        /// <param name="result">
        /// What every ad will report. Flip this to <see cref="AdResult.Skipped"/> and play the game
        /// for five minutes — if anything is granted, there is a payout bug.
        /// </param>
        public SimulatedAdsService(AdResult result = AdResult.Completed)
        {
            _result = result;
        }

        public bool IsRewardedReady => true;

        public bool IsInterstitialReady => !_interstitialsDisabled;

        public void Preload()
        {
        }

        public void ShowRewarded(AdPlacement placement, Action<AdResult> onFinished)
        {
            Debug.Log($"[SimulatedAds] Rewarded '{placement}' -> {_result}");
            onFinished?.Invoke(_result);
        }

        public void ShowInterstitial(AdPlacement placement, Action<AdResult> onFinished)
        {
            if (_interstitialsDisabled)
            {
                onFinished?.Invoke(AdResult.NotAvailable);
                return;
            }

            Debug.Log($"[SimulatedAds] Interstitial '{placement}' -> {_result}");
            onFinished?.Invoke(_result);
        }

        public void DisableInterstitials() => _interstitialsDisabled = true;
    }
}
