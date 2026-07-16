using System;
using NeonRush.Domain.Ads;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Run;
using NeonRush.Domain.Store;

namespace NeonRush.Application.Config
{
    /// <summary>
    /// Turns raw remote-config values into the validated configuration objects the game actually
    /// runs on.
    ///
    /// This class is the trust boundary for remote data, and that is its entire reason to exist.
    /// Remote Config values arrive over the network. Even from a legitimate backend they can be
    /// wrong — a designer fat-fingers a zero, a copy-paste puts the max speed below the base speed,
    /// a decimal point lands in the wrong place. And a value that is not clamped is a value that can
    /// brick the game for every player at once, remotely, with no way to roll back except another
    /// push.
    ///
    /// So every value is <b>clamped to a range that is always playable</b> before it reaches the
    /// game. The remote config decides where inside the safe range a number sits; it can never push
    /// a number outside it. A malicious `run_base_speed = 999999` becomes the clamp ceiling, not a
    /// player teleporting off the end of the world. This is defence in depth: the backend should not
    /// send bad values, but the client must survive them if it does.
    ///
    /// Pure C#. The clamping logic — the part that must be correct — is fully unit-testable with a
    /// seeded LocalRemoteConfig and no network.
    /// </summary>
    public sealed class GameConfigService
    {
        private readonly IRemoteConfigService _remote;

        public GameConfigService(IRemoteConfigService remote)
        {
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        }

        // -------------------------------------------------------------------------------
        // Run tuning
        // -------------------------------------------------------------------------------

        /// <summary>Builds a validated <see cref="RunTuning"/> from remote values over the compiled defaults.</summary>
        public RunTuning BuildRunTuning()
        {
            // Start from the defaults — a complete, balanced config. Remote values only move numbers
            // that are actually present; everything else keeps its shipped value.
            var d = new RunTuning();

            var tuning = new RunTuning
            {
                BaseSpeed = Clamp(_remote.GetFloat(RemoteConfigKeys.BaseSpeed, d.BaseSpeed), 3f, 20f),
                MaxSpeed = Clamp(_remote.GetFloat(RemoteConfigKeys.MaxSpeed, d.MaxSpeed), 3f, 40f),
                SpeedGainPerMetre = Clamp(_remote.GetFloat(RemoteConfigKeys.SpeedGainPerMetre, d.SpeedGainPerMetre), 0f, 1f),
                SafeStartDistance = Clamp(_remote.GetFloat(RemoteConfigKeys.SafeStartDistance, d.SafeStartDistance), 0f, 500f),
                ScorePerCoin = ClampInt(_remote.GetInt(RemoteConfigKeys.ScorePerCoin, d.ScorePerCoin), 0, 10_000),

                // Fields not exposed to remote config keep their defaults.
                LaneWidth = d.LaneWidth,
                LaneChangeDuration = d.LaneChangeDuration,
                JumpVelocity = d.JumpVelocity,
                Gravity = d.Gravity,
                SlideDuration = d.SlideDuration,
                SlideHeightFactor = d.SlideHeightFactor,
                ScorePerMetre = d.ScorePerMetre,
                ChunkLength = d.ChunkLength,
                ActiveChunks = d.ActiveChunks,
                ChunkDespawnZ = d.ChunkDespawnZ,
            };

            // A cross-field invariant no single clamp can guarantee: max speed must be >= base speed,
            // or the game slows down as it progresses. If a bad push inverts them, repair it rather
            // than letting RunTuning.Validate throw and take the boot down with it.
            if (tuning.MaxSpeed < tuning.BaseSpeed)
            {
                tuning.MaxSpeed = tuning.BaseSpeed;
            }

            // Final guard: if the result is somehow still invalid, fall back entirely to defaults.
            // A live game on safe defaults beats a crashed game on a clever config.
            try
            {
                tuning.Validate();
                return tuning;
            }
            catch (ArgumentException)
            {
                return d;
            }
        }

        // -------------------------------------------------------------------------------
        // Ad policy
        // -------------------------------------------------------------------------------

        /// <summary>Builds a validated <see cref="AdPolicyConfig"/> from remote values over the compiled defaults.</summary>
        public AdPolicyConfig BuildAdPolicyConfig()
        {
            var d = new AdPolicyConfig();

            var config = new AdPolicyConfig
            {
                // The grace period is the most important ad number, so it gets the most conservative
                // treatment: a remote value can raise it (more protective) freely, but the clamp
                // floor guarantees new players are shielded even if someone sets it to zero.
                GraceRuns = ClampInt(_remote.GetInt(RemoteConfigKeys.AdGraceRuns, d.GraceRuns), 0, 100),

                RunsBetweenInterstitials = ClampInt(_remote.GetInt(RemoteConfigKeys.AdRunsBetween, d.RunsBetweenInterstitials), 1, 50),
                SecondsBetweenInterstitials = Clamp(_remote.GetFloat(RemoteConfigKeys.AdSecondsBetween, d.SecondsBetweenInterstitials), 0f, 3600f),
                MinimumRunSecondsForInterstitial = Clamp(_remote.GetFloat(RemoteConfigKeys.AdMinRunSeconds, d.MinimumRunSecondsForInterstitial), 0f, 300f),

                // The session cap is the last line of defence against a hostile push cranking every
                // other number toward "show ads constantly". Its own clamp ceiling keeps even a
                // maxed-out config from becoming an ad every run.
                MaxInterstitialsPerSession = ClampInt(_remote.GetInt(RemoteConfigKeys.AdMaxPerSession, d.MaxInterstitialsPerSession), 0, 20),
            };

            try
            {
                config.Validate();
                return config;
            }
            catch (ArgumentException)
            {
                return d;
            }
        }

        // -------------------------------------------------------------------------------
        // Store prices
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Overrides the catalogue's currency prices from remote config.
        ///
        /// Only currency prices (coins/gems) are touched. Real-money prices come from the platform
        /// store — App Store Connect and the Play Console — and cannot be changed from here; trying
        /// to would just make the displayed price disagree with what the player is charged.
        ///
        /// Each price is clamped to a floor of 1: a remote value of 0 or negative would make an item
        /// free or, worse, pay the player to take it, which is an instant economy exploit the moment
        /// it goes live.
        /// </summary>
        public void ApplyStorePrices(StoreCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            foreach (var item in catalog.All)
            {
                if (item.Price.IsRealMoney) continue;

                var key = RemoteConfigKeys.PriceKeyFor(item.Id);
                var overridden = _remote.GetInt(key, item.Price.Amount);

                if (overridden == item.Price.Amount) continue; // no remote override for this item

                var safe = Math.Max(1, overridden);
                item.OverrideCurrencyPrice(safe);
            }
        }

        // -------------------------------------------------------------------------------
        // Feature flags
        // -------------------------------------------------------------------------------

        /// <summary>Whether the "double your coins" rewarded offer is enabled. Lets LiveOps kill a misbehaving offer instantly.</summary>
        public bool DoubleCoinsEnabled => _remote.GetBool(RemoteConfigKeys.DoubleCoinsEnabled, true);

        /// <summary>Whether the revive offer is enabled.</summary>
        public bool ReviveEnabled => _remote.GetBool(RemoteConfigKeys.ReviveEnabled, true);

        private static float Clamp(float value, float min, float max) =>
            value < min ? min : value > max ? max : value;

        private static int ClampInt(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;
    }
}
