using System;
using System.Collections.Generic;
using NeonRush.Application.Ads;
using NeonRush.Application.Economy;
using NeonRush.Application.Events;
using NeonRush.Application.Progression;
using NeonRush.Core.Events;
using NeonRush.Domain.Ads;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Ports;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>A fully controllable ad network. Every failure mode is a dial.</summary>
    internal sealed class FakeAdsService : IAdsService
    {
        public bool IsRewardedReady { get; set; } = true;
        public bool IsInterstitialReady { get; set; } = true;

        /// <summary>What the next ad reports.</summary>
        public AdResult Result { get; set; } = AdResult.Completed;

        public readonly List<AdPlacement> Shown = new();
        public int PreloadCount { get; private set; }
        public bool InterstitialsDisabled { get; private set; }

        public void Preload() => PreloadCount++;

        public void ShowRewarded(AdPlacement placement, Action<AdResult> onFinished)
        {
            Shown.Add(placement);
            onFinished?.Invoke(Result);
        }

        public void ShowInterstitial(AdPlacement placement, Action<AdResult> onFinished)
        {
            Shown.Add(placement);
            onFinished?.Invoke(Result);
        }

        public void DisableInterstitials() => InterstitialsDisabled = true;
    }

    [TestFixture]
    public sealed class AdPolicyTests
    {
        private FakeClock _clock;
        private AdPolicyConfig _config;
        private AdPolicy _policy;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _config = new AdPolicyConfig();
            _policy = new AdPolicy(_config, _clock);
        }

        /// <summary>Advances the monotonic clock.</summary>
        private void Wait(float seconds) =>
            _clock.ElapsedRealtime += TimeSpan.FromSeconds(seconds);

        /// <summary>Satisfies the "runs since last ad" gate.</summary>
        private void PlayRuns(int count)
        {
            for (var i = 0; i < count; i++) _policy.RecordRunCompleted();
        }

        private const int VeteranRuns = 100;
        private const float LongRun = 60f;

        [Test]
        public void ANewPlayerIsNeverShownAnInterstitial()
        {
            // The most valuable rule in the file. D1 retention multiplies every other revenue line,
            // and an ad shown to someone who has played twice earns fractions of a cent while
            // measurably reducing the odds they ever come back. You cannot monetise an uninstall.
            PlayRuns(50);
            Wait(10_000f);

            for (var run = 1; run <= _config.GraceRuns; run++)
            {
                var decision = _policy.ShouldShowInterstitial(lifetimeRuns: run, lastRunSeconds: LongRun);

                Assert.That(decision.Allowed, Is.False, $"Run {run} is inside the grace period.");
                Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.NewPlayerGracePeriod));
            }
        }

        [Test]
        public void AVeteranPlayerWhoHasWaited_GetsAnAd()
        {
            PlayRuns(_config.RunsBetweenInterstitials);

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, LongRun);

            Assert.That(decision.Allowed, Is.True, decision.Refusal.ToString());
        }

        [Test]
        public void AFrustratingRunIsNeverTaxed()
        {
            // A player who died in four seconds is angry and wants to retry immediately. An ad here
            // is the moment they close the app for good.
            PlayRuns(50);
            Wait(10_000f);

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, lastRunSeconds: 4f);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.RunTooShort));
        }

        [Test]
        public void ThreeFastDeathsCannotBypassTheCooldown()
        {
            // This is the bug the time gate exists for. With only a run counter, a player who dies
            // three times in twenty seconds has "completed" three runs — and gets shown a 30-second
            // unskippable ad for twenty seconds of play. That is precisely how a game earns
            // "this is just ads" reviews.
            PlayRuns(_config.RunsBetweenInterstitials);
            Assert.That(_policy.ShouldShowInterstitial(VeteranRuns, LongRun).Allowed, Is.True);

            _policy.RecordInterstitialShown();

            // Now three more long runs, but only 20 seconds of wall-clock have passed.
            PlayRuns(_config.RunsBetweenInterstitials);
            Wait(20f);

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, LongRun);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.CooldownActive),
                "The run counter alone must not be able to unlock an ad.");
        }

        [Test]
        public void AfterTheCooldownExpires_AdsResume()
        {
            PlayRuns(_config.RunsBetweenInterstitials);
            _policy.ShouldShowInterstitial(VeteranRuns, LongRun);
            _policy.RecordInterstitialShown();

            PlayRuns(_config.RunsBetweenInterstitials);
            Wait(_config.SecondsBetweenInterstitials + 1f);

            Assert.That(_policy.ShouldShowInterstitial(VeteranRuns, LongRun).Allowed, Is.True);
        }

        [Test]
        public void TooFewRunsSinceTheLastAd_IsRefused()
        {
            PlayRuns(_config.RunsBetweenInterstitials);
            _policy.ShouldShowInterstitial(VeteranRuns, LongRun);
            _policy.RecordInterstitialShown();

            Wait(10_000f); // cooldown is long gone
            _policy.RecordRunCompleted(); // but only one run since

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, LongRun);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.TooFewRunsSinceLastAd));
        }

        [Test]
        public void TheSessionCapIsAHardBackstop()
        {
            // Protects against a misconfigured Remote Config pushing ad frequency to something
            // hostile. A remote value should never be able to make the game unplayable.
            for (var i = 0; i < _config.MaxInterstitialsPerSession; i++)
            {
                PlayRuns(_config.RunsBetweenInterstitials);
                Wait(_config.SecondsBetweenInterstitials + 1f);

                Assert.That(_policy.ShouldShowInterstitial(VeteranRuns, LongRun).Allowed, Is.True);
                _policy.RecordInterstitialShown();
            }

            PlayRuns(_config.RunsBetweenInterstitials);
            Wait(_config.SecondsBetweenInterstitials + 1f);

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, LongRun);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.SessionCapReached));
        }

        [Test]
        public void APayingPlayerNeverSeesAnInterstitial()
        {
            _policy.DisableInterstitials();

            PlayRuns(100);
            Wait(10_000f);

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, LongRun);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.PlayerPaidToRemoveAds));
        }

        [Test]
        public void TheClockGateUsesMonotonicTime_NotTheWallClock()
        {
            // If the cooldown ran off the wall clock, a player could skip every ad by winding their
            // device clock forward. AdPolicy reads IClock.ElapsedRealtime, which cannot be moved.
            PlayRuns(_config.RunsBetweenInterstitials);
            _policy.ShouldShowInterstitial(VeteranRuns, LongRun);
            _policy.RecordInterstitialShown();

            PlayRuns(_config.RunsBetweenInterstitials);

            // Wind the wall clock forward a year. The monotonic clock does not move.
            _clock.UtcNow = _clock.UtcNow.AddYears(1);

            var decision = _policy.ShouldShowInterstitial(VeteranRuns, LongRun);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Refusal, Is.EqualTo(AdRefusal.CooldownActive),
                "Changing the device clock must not unlock ads.");
        }

        [Test]
        public void AnInvalidConfigIsRejected()
        {
            Assert.Throws<ArgumentException>(
                () => new AdPolicy(new AdPolicyConfig { RunsBetweenInterstitials = 0 }, _clock));
        }
    }

    [TestFixture]
    public sealed class AdDirectorTests
    {
        private EventBus _bus;
        private FakeAdsService _ads;
        private FakeClock _clock;
        private Wallet _wallet;
        private RunRewardService _rewards;
        private PlayerProfile _profile;
        private AdPolicy _policy;
        private AdDirector _director;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _ads = new FakeAdsService();
            _clock = new FakeClock();
            _wallet = new Wallet(_bus);
            _rewards = new RunRewardService(_wallet, _bus);
            _profile = new PlayerProfile(_bus);
            _policy = new AdPolicy(new AdPolicyConfig(), _clock);
            _director = new AdDirector(_ads, _policy, _rewards, _profile, _bus);
        }

        [TearDown]
        public void TearDown()
        {
            _director.Dispose();
            _profile.Dispose();
            _rewards.Dispose();
            _bus.Dispose();
        }

        private void EndRun(int coins = 40, float seconds = 60f) =>
            _bus.Publish(new RunEnded(1, 500f, coins, 900, DeathCause.HitObstacle, seconds));

        [Test]
        public void AdsArePreloadedAtBoot()
        {
            // An ad that is not already in memory when the player taps "Revive" is a four-second
            // spinner, and a button that spins is a button nobody presses twice.
            Assert.That(_ads.PreloadCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void DoubleCoins_GrantsOnlyWhenTheAdIsWatchedToCompletion()
        {
            EndRun(coins: 40);
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(40), "Base reward is banked first.");

            var granted = 0;
            _director.OfferDoubleCoins(g => granted = g);

            Assert.That(granted, Is.EqualTo(40));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(80));
        }

        [Test]
        public void DoubleCoins_GrantsNothingWhenTheAdIsSkipped()
        {
            // The single most expensive bug in ad code: paying out on the wrong callback. A player
            // who taps and immediately swipes away has not fulfilled the bargain.
            EndRun(coins: 40);

            _ads.Result = AdResult.Skipped;

            var granted = -1;
            _director.OfferDoubleCoins(g => granted = g);

            Assert.That(granted, Is.Zero);
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(40),
                "A skipped ad must grant nothing.");
        }

        [Test]
        public void DoubleCoins_GrantsNothingWhenTheAdFails()
        {
            EndRun(coins: 40);
            _ads.Result = AdResult.Failed;

            _director.OfferDoubleCoins(_ => { });

            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(40));
        }

        [Test]
        public void DoubleCoins_IsNotOfferedWhenNoAdIsLoaded()
        {
            // Promising a reward and then failing to produce an ad is worse than never offering it.
            EndRun(coins: 40);
            _ads.IsRewardedReady = false;

            Assert.That(_director.CanOfferDoubleCoins, Is.False);
        }

        [Test]
        public void DoubleCoins_IsNotOfferedForAZeroCoinRun()
        {
            EndRun(coins: 0);

            Assert.That(_director.CanOfferDoubleCoins, Is.False,
                "Offering to double nothing burns an impression and insults the player.");
        }

        [Test]
        public void Revive_OnlySucceedsOnACompletedAd()
        {
            var revived = false;
            _director.OfferRevive(revivesUsed: 0, ok => revived = ok);

            Assert.That(revived, Is.True);
            Assert.That(_ads.Shown, Has.Member(AdPlacement.Revive));
        }

        [Test]
        public void Revive_FailsWhenTheAdIsSkipped()
        {
            _ads.Result = AdResult.Skipped;

            var revived = true;
            _director.OfferRevive(revivesUsed: 0, ok => revived = ok);

            Assert.That(revived, Is.False);
        }

        [Test]
        public void Revive_IsCappedPerRun()
        {
            // Unlimited revives turn the leaderboard into a measure of patience rather than skill,
            // and dissolve the tension that makes the run worth playing.
            Assert.That(_director.CanOfferRevive(revivesUsed: 0), Is.True);
            Assert.That(_director.CanOfferRevive(revivesUsed: AdDirector.MaxAdRevivesPerRun), Is.False);
        }

        [Test]
        public void Interstitial_IsSuppressedForANewPlayer()
        {
            EndRun(seconds: 90f); // profile.TotalRuns is now 1

            var done = false;
            _director.MaybeShowInterstitial(90f, () => done = true);

            Assert.That(done, Is.True, "The callback must always fire, ad or no ad.");
            Assert.That(_ads.Shown, Has.No.Member(AdPlacement.BetweenRuns));
            Assert.That(_director.LastInterstitialRefusal, Is.EqualTo(AdRefusal.NewPlayerGracePeriod));
        }

        [Test]
        public void Interstitial_ShowsForAVeteranAfterEnoughRuns()
        {
            for (var i = 0; i < 10; i++) EndRun(seconds: 90f);

            _clock.ElapsedRealtime = TimeSpan.FromHours(1);

            var done = false;
            _director.MaybeShowInterstitial(90f, () => done = true);

            Assert.That(done, Is.True);
            Assert.That(_ads.Shown, Has.Member(AdPlacement.BetweenRuns));
        }

        [Test]
        public void Interstitial_WithNoFill_DoesNotBlockTheGame()
        {
            // No fill is not a failure. Never make the player wait for an ad we do not have.
            for (var i = 0; i < 10; i++) EndRun(seconds: 90f);
            _clock.ElapsedRealtime = TimeSpan.FromHours(1);

            _ads.IsInterstitialReady = false;

            var done = false;
            _director.MaybeShowInterstitial(90f, () => done = true);

            Assert.That(done, Is.True, "The game must continue even with no ad available.");
            Assert.That(_director.LastInterstitialRefusal, Is.EqualTo(AdRefusal.NotLoaded));
        }

        [Test]
        public void TheCallbackAlwaysFires_EvenWhenNoAdIsShown()
        {
            // If this ever fails to fire, the game soft-locks on the game-over screen forever. It is
            // the single worst bug an ad integration can have, and it is entirely preventable.
            _ads.IsRewardedReady = false;
            _ads.IsInterstitialReady = false;

            var reviveDone = false;
            var doubleDone = false;
            var interstitialDone = false;

            _director.OfferRevive(0, _ => reviveDone = true);
            _director.OfferDoubleCoins(_ => doubleDone = true);
            _director.MaybeShowInterstitial(90f, () => interstitialDone = true);

            Assert.That(reviveDone, Is.True);
            Assert.That(doubleDone, Is.True);
            Assert.That(interstitialDone, Is.True);
        }
    }
}
