using System;
using NeonRush.Application.Economy;
using NeonRush.Application.Events;
using NeonRush.Application.Progression;
using NeonRush.Core.Events;
using NeonRush.Domain.Ads;
using NeonRush.Domain.Ports;

namespace NeonRush.Application.Ads
{
    /// <summary>
    /// Owns every ad decision in the game.
    ///
    /// One class, so that "when does this player see an ad?" has exactly one answer, in one place,
    /// that can be read in a minute and tested in a millisecond. The alternative — ad calls sprinkled
    /// through the UI wherever they seemed convenient — is how games end up showing an interstitial
    /// on top of a rewarded ad, or interrupting a player mid-run, and nobody can work out why.
    ///
    /// The two ad products are governed completely differently, and the distinction is the whole
    /// design:
    ///
    ///  · <b>Rewarded</b> ads are a shop. The player walks in voluntarily and trades attention for
    ///    something they want. There is no cooldown, no cap, no grace period — a player who wants to
    ///    watch ten ads should be allowed to. The only rules are: never promise what you cannot
    ///    deliver (check <see cref="IAdsService.IsRewardedReady"/> before offering), and never pay
    ///    out unless they actually watched it.
    ///
    ///  · <b>Interstitials</b> are a tax. The player did not ask, gains nothing, and every one spends
    ///    a little of the goodwill that keeps them installed. These are governed by
    ///    <see cref="AdPolicy"/>, aggressively.
    /// </summary>
    public sealed class AdDirector : IDisposable
    {
        private readonly IAdsService _ads;
        private readonly AdPolicy _policy;
        private readonly RunRewardService _rewards;
        private readonly PlayerProfile _profile;
        private readonly IEventBus _bus;
        private readonly IDisposable _subscription;

        /// <summary>Guards against two overlapping ad shows, which crashes most SDKs.</summary>
        private bool _adInFlight;

        public AdDirector(
            IAdsService ads,
            AdPolicy policy,
            RunRewardService rewards,
            PlayerProfile profile,
            IEventBus bus)
        {
            _ads = ads ?? throw new ArgumentNullException(nameof(ads));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            _subscription = _bus.Subscribe<RunEnded>(OnRunEnded);

            // Preload at boot. An ad that is not already in memory when the player taps "Revive" is
            // a four-second spinner, and a button that spins is a button nobody presses twice.
            _ads.Preload();
        }

        /// <summary>The last interstitial decision. Reported to analytics so ad yield is diagnosable.</summary>
        public AdRefusal LastInterstitialRefusal { get; private set; }

        /// <summary>True while an ad is showing. The UI must not offer another one.</summary>
        public bool IsAdInFlight => _adInFlight;

        private void OnRunEnded(RunEnded e)
        {
            // A revive keeps the same run alive, so a revived run must not be counted twice toward
            // the ad cadence. Only the final, real end of a run advances the counter.
            _policy.RecordRunCompleted();
        }

        // -------------------------------------------------------------------------------
        // Rewarded: Double coins
        // -------------------------------------------------------------------------------

        /// <summary>True when the "double your coins" offer can actually be honoured.</summary>
        public bool CanOfferDoubleCoins =>
            !_adInFlight && _rewards.CanClaimDouble && _ads.IsRewardedReady;

        /// <summary>
        /// Shows the doubling ad and, only if it is watched to completion, credits the coins again.
        /// </summary>
        /// <param name="onComplete">Coins added (0 if the player skipped or the ad failed).</param>
        public void OfferDoubleCoins(Action<int> onComplete)
        {
            if (!CanOfferDoubleCoins)
            {
                onComplete?.Invoke(0);
                return;
            }

            _adInFlight = true;

            _ads.ShowRewarded(AdPlacement.DoubleCoins, result =>
            {
                _adInFlight = false;
                _ads.Preload();

                // ONLY on Completed. Granting on dismiss, on close, or on "the SDK called us back
                // somehow" is how a game hands out currency to anyone who taps and immediately
                // swipes away.
                var granted = result == AdResult.Completed ? _rewards.ClaimDouble() : 0;

                onComplete?.Invoke(granted);
            });
        }

        // -------------------------------------------------------------------------------
        // Rewarded: Revive
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Revives are capped per run.
        ///
        /// Not for technical reasons — for design ones. Unlimited revives turn the leaderboard into a
        /// measure of patience rather than skill, and they dissolve the tension that makes the run
        /// worth playing. One free (ad-funded) revive is the sweet spot: it rescues the heartbreaking
        /// death without making death meaningless.
        /// </summary>
        public const int MaxAdRevivesPerRun = 1;

        /// <summary>True when a revive can be offered for this run.</summary>
        public bool CanOfferRevive(int revivesUsed) =>
            !_adInFlight && revivesUsed < MaxAdRevivesPerRun && _ads.IsRewardedReady;

        /// <summary>
        /// Shows the revive ad. Calls back with true only if the ad was watched to completion, in
        /// which case the caller must resume the run.
        /// </summary>
        public void OfferRevive(int revivesUsed, Action<bool> onComplete)
        {
            if (!CanOfferRevive(revivesUsed))
            {
                onComplete?.Invoke(false);
                return;
            }

            _adInFlight = true;

            _ads.ShowRewarded(AdPlacement.Revive, result =>
            {
                _adInFlight = false;
                _ads.Preload();

                onComplete?.Invoke(result == AdResult.Completed);
            });
        }

        // -------------------------------------------------------------------------------
        // Interstitial
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Shows an interstitial if — and only if — the policy allows it.
        ///
        /// Called when the player leaves the game-over screen, never during a run. That is structural,
        /// not a rule: this is the only place in the codebase that calls it.
        /// </summary>
        /// <param name="lastRunSeconds">Duration of the run that just ended.</param>
        /// <param name="onComplete">Invoked when the ad closes, or immediately if none was shown.</param>
        public void MaybeShowInterstitial(float lastRunSeconds, Action onComplete)
        {
            if (_adInFlight)
            {
                onComplete?.Invoke();
                return;
            }

            var decision = _policy.ShouldShowInterstitial(_profile.TotalRuns, lastRunSeconds);

            if (!decision.Allowed)
            {
                LastInterstitialRefusal = decision.Refusal;
                onComplete?.Invoke();
                return;
            }

            if (!_ads.IsInterstitialReady)
            {
                // No fill. Not a failure — just carry on. Never make the player wait for an ad we do
                // not have; a loading spinner in front of an unskippable ad is the worst of both.
                LastInterstitialRefusal = AdRefusal.NotLoaded;
                _ads.Preload();
                onComplete?.Invoke();
                return;
            }

            LastInterstitialRefusal = AdRefusal.None;
            _adInFlight = true;

            _ads.ShowInterstitial(AdPlacement.BetweenRuns, _ =>
            {
                _adInFlight = false;

                // Record on the impression, not on the decision. An ad we decided to show but failed
                // to display must not consume the cooldown, or a run of ad failures would silently
                // starve us of the ads that do work.
                _policy.RecordInterstitialShown();
                _ads.Preload();

                onComplete?.Invoke();
            });
        }

        public void Dispose() => _subscription.Dispose();
    }
}
