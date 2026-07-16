using System.Collections.Generic;
using NeonRush.Application.Config;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;
using NeonRush.Domain.Economy;
using NeonRush.Infrastructure.Remote;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    [TestFixture]
    public sealed class GameConfigServiceTests
    {
        private static GameConfigService WithConfig(params (string key, string value)[] values)
        {
            var seed = new Dictionary<string, string>();
            foreach (var (key, value) in values) seed[key] = value;

            return new GameConfigService(new LocalRemoteConfig(seed));
        }

        // -------------------------------------------------------------------------------
        // Defaults-first: the offline day-one experience must be complete
        // -------------------------------------------------------------------------------

        [Test]
        public void WithNoRemoteValues_TheGameGetsAValidDefaultConfig()
        {
            // The core promise: a player who launches with no network gets a complete, balanced game.
            var config = WithConfig();

            var tuning = config.BuildRunTuning();
            Assert.DoesNotThrow(() => tuning.Validate());

            var adConfig = config.BuildAdPolicyConfig();
            Assert.DoesNotThrow(() => adConfig.Validate());
        }

        [Test]
        public void ADefaultConfigMatchesTheCompiledDefaults()
        {
            var config = WithConfig();
            var tuning = config.BuildRunTuning();
            var defaults = new NeonRush.Domain.Run.RunTuning();

            Assert.That(tuning.BaseSpeed, Is.EqualTo(defaults.BaseSpeed));
            Assert.That(tuning.MaxSpeed, Is.EqualTo(defaults.MaxSpeed));
        }

        // -------------------------------------------------------------------------------
        // Remote values are actually applied
        // -------------------------------------------------------------------------------

        [Test]
        public void ARemoteValue_OverridesTheDefault()
        {
            var config = WithConfig((RemoteConfigKeys.BaseSpeed, "12"));

            Assert.That(config.BuildRunTuning().BaseSpeed, Is.EqualTo(12f));
        }

        [Test]
        public void RemoteAdFrequency_IsApplied()
        {
            var config = WithConfig(
                (RemoteConfigKeys.AdGraceRuns, "10"),
                (RemoteConfigKeys.AdRunsBetween, "5"));

            var adConfig = config.BuildAdPolicyConfig();

            Assert.That(adConfig.GraceRuns, Is.EqualTo(10));
            Assert.That(adConfig.RunsBetweenInterstitials, Is.EqualTo(5));
        }

        [Test]
        public void FloatsParseWithInvariantCulture()
        {
            // A backend sends "0.02" with a dot, always. Parsing it under a comma-decimal locale
            // would silently read 2 instead of 0.02 and make the game wildly too hard.
            var config = WithConfig((RemoteConfigKeys.SpeedGainPerMetre, "0.02"));

            Assert.That(config.BuildRunTuning().SpeedGainPerMetre, Is.EqualTo(0.02f).Within(0.0001f));
        }

        // -------------------------------------------------------------------------------
        // The point of the whole class: hostile / malformed values are clamped, never applied raw
        // -------------------------------------------------------------------------------

        [Test]
        public void AnAbsurdlyHighSpeed_IsClampedNotApplied()
        {
            // A fat-fingered or malicious push of a huge speed must not teleport the player off the
            // end of the world. The clamp ceiling holds.
            var config = WithConfig((RemoteConfigKeys.MaxSpeed, "999999"));

            var tuning = config.BuildRunTuning();

            Assert.That(tuning.MaxSpeed, Is.LessThanOrEqualTo(40f));
            Assert.DoesNotThrow(() => tuning.Validate());
        }

        [Test]
        public void AZeroBaseSpeed_IsClampedToAPlayableFloor()
        {
            // base speed 0 = the player never moves = the game is bricked for everyone, remotely.
            var config = WithConfig((RemoteConfigKeys.BaseSpeed, "0"));

            var tuning = config.BuildRunTuning();

            Assert.That(tuning.BaseSpeed, Is.GreaterThanOrEqualTo(3f));
            Assert.DoesNotThrow(() => tuning.Validate());
        }

        [Test]
        public void MaxSpeedBelowBaseSpeed_IsRepairedNotCrashed()
        {
            // A config where max < base would make the game slow down as it progresses, and would
            // throw in RunTuning.Validate. The service must repair the invariant, not let the boot die.
            var config = WithConfig(
                (RemoteConfigKeys.BaseSpeed, "18"),
                (RemoteConfigKeys.MaxSpeed, "9"));

            var tuning = config.BuildRunTuning();

            Assert.That(tuning.MaxSpeed, Is.GreaterThanOrEqualTo(tuning.BaseSpeed));
            Assert.DoesNotThrow(() => tuning.Validate());
        }

        [Test]
        public void AHostileAdConfig_CannotMakeAdsConstant()
        {
            // Someone tries to push "an ad every run, no cooldown, no grace, huge session cap".
            // Every one of those is clamped, and the game stays inside a humane range.
            var config = WithConfig(
                (RemoteConfigKeys.AdGraceRuns, "0"),
                (RemoteConfigKeys.AdRunsBetween, "0"),          // would be "every run"
                (RemoteConfigKeys.AdSecondsBetween, "-5"),      // would be "no cooldown"
                (RemoteConfigKeys.AdMaxPerSession, "100000"));  // would be "unlimited"

            var adConfig = config.BuildAdPolicyConfig();

            Assert.That(adConfig.RunsBetweenInterstitials, Is.GreaterThanOrEqualTo(1),
                "Runs-between must never be zero, or every run shows an ad.");
            Assert.That(adConfig.SecondsBetweenInterstitials, Is.GreaterThanOrEqualTo(0f));
            Assert.That(adConfig.MaxInterstitialsPerSession, Is.LessThanOrEqualTo(20));
            Assert.DoesNotThrow(() => adConfig.Validate());
        }

        [Test]
        public void AGarbageValue_FallsBackToTheDefault()
        {
            // "fast" is not a number. It must be ignored, not crash the parse.
            var config = WithConfig((RemoteConfigKeys.BaseSpeed, "fast"));
            var defaults = new NeonRush.Domain.Run.RunTuning();

            Assert.That(config.BuildRunTuning().BaseSpeed, Is.EqualTo(defaults.BaseSpeed));
        }

        // -------------------------------------------------------------------------------
        // Store prices
        // -------------------------------------------------------------------------------

        [Test]
        public void RemoteStorePrice_OverridesTheCurrencyPrice()
        {
            var config = WithConfig((RemoteConfigKeys.PriceKeyFor("char_nova"), "1500"));
            var catalog = StoreCatalog.Default();

            config.ApplyStorePrices(catalog);

            Assert.That(catalog.TryGet("char_nova", out var item), Is.True);
            Assert.That(item.Price.Amount, Is.EqualTo(1500));
        }

        [Test]
        public void AFreeOrNegativePrice_IsClampedToAtLeastOne()
        {
            // A remote price of 0 would make the item free; a negative one would pay the player to
            // take it. Either is an instant economy exploit the moment it goes live.
            var config = WithConfig((RemoteConfigKeys.PriceKeyFor("char_nova"), "0"));
            var catalog = StoreCatalog.Default();

            config.ApplyStorePrices(catalog);

            catalog.TryGet("char_nova", out var item);
            Assert.That(item.Price.Amount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void RealMoneyPrices_AreNeverTouchedByRemoteConfig()
        {
            // Real-money prices are owned by the platform store. Trying to override one from here
            // would make the shown price disagree with what the player is charged — a support
            // nightmare, and in several countries illegal.
            var config = WithConfig((RemoteConfigKeys.PriceKeyFor("gems_500"), "1"));
            var catalog = StoreCatalog.Default();

            Assert.DoesNotThrow(() => config.ApplyStorePrices(catalog));

            catalog.TryGet("gems_500", out var item);
            Assert.That(item.Price.IsRealMoney, Is.True);
            Assert.That(item.Price.ProductId, Is.EqualTo("com.mooncatstudio.neonrush.gems_500"));
        }

        [Test]
        public void OverridingARealMoneyPriceDirectly_Throws()
        {
            var catalog = StoreCatalog.Default();
            catalog.TryGet("gems_500", out var item);

            Assert.Throws<System.InvalidOperationException>(() => item.OverrideCurrencyPrice(1));
        }

        // -------------------------------------------------------------------------------
        // Feature flags
        // -------------------------------------------------------------------------------

        [Test]
        public void FeatureFlagsDefaultToEnabled()
        {
            var config = WithConfig();

            Assert.That(config.DoubleCoinsEnabled, Is.True);
            Assert.That(config.ReviveEnabled, Is.True);
        }

        [Test]
        public void AFeatureCanBeKilledRemotely()
        {
            // The kill switch: a misbehaving offer can be turned off in minutes without a build.
            var config = WithConfig((RemoteConfigKeys.DoubleCoinsEnabled, "false"));

            Assert.That(config.DoubleCoinsEnabled, Is.False);
        }
    }

    [TestFixture]
    public sealed class LocalRemoteConfigTests
    {
        [Test]
        public void IsReadyImmediately_NoFetchToWaitFor()
        {
            Assert.That(new LocalRemoteConfig().IsReady, Is.True);
        }

        [Test]
        public void FetchReportsSuccessSynchronously()
        {
            var fetched = false;
            new LocalRemoteConfig().Fetch(ok => fetched = ok);

            Assert.That(fetched, Is.True);
        }

        [Test]
        public void ReturnsDefaultForAMissingKey()
        {
            var config = new LocalRemoteConfig();

            Assert.That(config.GetInt("nope", 42), Is.EqualTo(42));
            Assert.That(config.GetString("nope", "x"), Is.EqualTo("x"));
            Assert.That(config.GetBool("nope", true), Is.True);
        }

        [Test]
        public void SetOverridesAtRuntime()
        {
            var config = new LocalRemoteConfig();
            config.Set("k", "7");

            Assert.That(config.GetInt("k", 0), Is.EqualTo(7));
        }
    }
}
