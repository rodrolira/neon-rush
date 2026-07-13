using System;
using NeonRush.Domain.Ports;

namespace NeonRush.Domain.Ads
{
    /// <summary>
    /// Every number that decides when a player sees an interstitial.
    ///
    /// All of these are Remote Config keys in the shipping game — this class holds the compiled-in
    /// defaults so that a player who launches offline on day one still gets a sane experience
    /// (ARCHITECTURE.md §9). Tuning ad frequency is the single most common LiveOps lever, and it
    /// must never require a store submission.
    /// </summary>
    public sealed class AdPolicyConfig
    {
        /// <summary>
        /// Runs a brand-new player completes before they may see their first interstitial.
        ///
        /// This is the highest-value number in the file. D1 retention is the multiplier on every
        /// other revenue line in the game, and an ad shown to someone who has played twice earns
        /// fractions of a cent while measurably reducing the chance they ever come back. You cannot
        /// monetise a player who uninstalled. Let them fall in love first.
        /// </summary>
        public int GraceRuns { get; set; } = 5;

        /// <summary>Minimum runs between two interstitials.</summary>
        public int RunsBetweenInterstitials { get; set; } = 3;

        /// <summary>
        /// Minimum seconds between two interstitials, regardless of how many runs happened.
        ///
        /// The run counter alone is not enough: a player who dies instantly three times in a row has
        /// "completed" three runs in twenty seconds, and without this they would be shown a
        /// 30-second unskippable ad for twenty seconds of play. That is the exact experience that
        /// produces "this game is just ads" reviews.
        /// </summary>
        public float SecondsBetweenInterstitials { get; set; } = 180f;

        /// <summary>
        /// A run shorter than this never triggers an interstitial.
        ///
        /// Interrupting someone who just died in four seconds — who is frustrated and wants to
        /// immediately retry — is the worst possible moment to take their attention. They are not
        /// going to watch it; they are going to close the app.
        /// </summary>
        public float MinimumRunSecondsForInterstitial { get; set; } = 25f;

        /// <summary>Hard cap on interstitials per session. A backstop against a misconfigured Remote Config.</summary>
        public int MaxInterstitialsPerSession { get; set; } = 6;

        public void Validate()
        {
            if (GraceRuns < 0) throw new ArgumentException($"{nameof(GraceRuns)} must be >= 0.");
            if (RunsBetweenInterstitials < 1) throw new ArgumentException($"{nameof(RunsBetweenInterstitials)} must be >= 1.");
            if (SecondsBetweenInterstitials < 0f) throw new ArgumentException($"{nameof(SecondsBetweenInterstitials)} must be >= 0.");
            if (MaxInterstitialsPerSession < 0) throw new ArgumentException($"{nameof(MaxInterstitialsPerSession)} must be >= 0.");
        }
    }

    /// <summary>
    /// Decides whether an interstitial may be shown. Pure, deterministic, clock-driven.
    ///
    /// The mental model that makes this file make sense: <b>ad revenue is a tax on attention, and
    /// attention is a stock, not a flow.</b> Every interstitial spends a little of the player's
    /// goodwill. Spend it faster than the game earns it back and the player leaves, and the lifetime
    /// value of a player who leaves is zero no matter how many ads you showed them on the way out.
    ///
    /// So the policy is deliberately conservative, and every rule below exists because its absence
    /// is a well-documented way to kill retention:
    ///
    ///  · New players are protected absolutely (<see cref="AdPolicyConfig.GraceRuns"/>).
    ///  · Ads are gated on BOTH runs and wall-clock time, because either alone is trivially
    ///    defeated by a player who keeps dying quickly.
    ///  · A frustrating run (a fast death) never earns an ad.
    ///  · Interstitials never appear during gameplay. That is not a policy here; it is structural —
    ///    the only thing that ever asks this class is the run-ended handler.
    ///
    /// Rewarded ads are NOT governed by this class at all, and that is the point: the player chooses
    /// them, so there is nothing to protect them from. A player who wants to watch ten ads to revive
    /// ten times should be allowed to.
    /// </summary>
    public sealed class AdPolicy
    {
        private readonly AdPolicyConfig _config;
        private readonly IClock _clock;

        /// <summary>Monotonic time of the last interstitial. Never the wall clock — see IClock.</summary>
        private TimeSpan _lastInterstitialAt = TimeSpan.MinValue;

        private int _runsSinceLastInterstitial;
        private int _interstitialsThisSession;
        private bool _interstitialsDisabled;

        public AdPolicy(AdPolicyConfig config, IClock clock)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _config.Validate();
        }

        /// <summary>Interstitials shown so far this session. Reported to analytics.</summary>
        public int InterstitialsThisSession => _interstitialsThisSession;

        /// <summary>True once the player has paid to remove ads.</summary>
        public bool InterstitialsDisabled => _interstitialsDisabled;

        /// <summary>Permanently disables interstitials. Rewarded ads remain available — see IAdsService.</summary>
        public void DisableInterstitials() => _interstitialsDisabled = true;

        /// <summary>
        /// Decides whether an interstitial may be shown now.
        /// </summary>
        /// <param name="lifetimeRuns">Total runs this player has ever completed, across all sessions.</param>
        /// <param name="lastRunSeconds">Duration of the run that just ended.</param>
        public AdDecision ShouldShowInterstitial(int lifetimeRuns, float lastRunSeconds)
        {
            if (_interstitialsDisabled)
            {
                return AdDecision.No(AdRefusal.PlayerPaidToRemoveAds);
            }

            // The new-player shield. Absolute, and checked first, because nothing else matters if
            // they uninstall on day one.
            if (lifetimeRuns <= _config.GraceRuns)
            {
                return AdDecision.No(AdRefusal.NewPlayerGracePeriod);
            }

            if (_interstitialsThisSession >= _config.MaxInterstitialsPerSession)
            {
                return AdDecision.No(AdRefusal.SessionCapReached);
            }

            // Do not tax a bad run. A player who died in four seconds is frustrated and wants to
            // retry immediately; an ad here is the moment they close the app for good.
            if (lastRunSeconds < _config.MinimumRunSecondsForInterstitial)
            {
                return AdDecision.No(AdRefusal.RunTooShort);
            }

            if (_runsSinceLastInterstitial < _config.RunsBetweenInterstitials)
            {
                return AdDecision.No(AdRefusal.TooFewRunsSinceLastAd);
            }

            // Wall-clock gate, on the monotonic clock so it cannot be skipped by changing the device
            // time. Without this, three fast deaths would satisfy the run counter in under a minute.
            if (_lastInterstitialAt != TimeSpan.MinValue)
            {
                var since = (_clock.ElapsedRealtime - _lastInterstitialAt).TotalSeconds;

                if (since < _config.SecondsBetweenInterstitials)
                {
                    return AdDecision.No(AdRefusal.CooldownActive);
                }
            }

            return AdDecision.Yes();
        }

        /// <summary>Records that an interstitial was actually shown. Call this ONLY on a real impression.</summary>
        public void RecordInterstitialShown()
        {
            _lastInterstitialAt = _clock.ElapsedRealtime;
            _runsSinceLastInterstitial = 0;
            _interstitialsThisSession++;
        }

        /// <summary>Records that a run finished. Drives the runs-between-ads counter.</summary>
        public void RecordRunCompleted() => _runsSinceLastInterstitial++;
    }

    /// <summary>Why an interstitial was refused. Every refusal is reported, so the funnel is visible.</summary>
    public enum AdRefusal
    {
        None = 0,
        NewPlayerGracePeriod = 1,
        TooFewRunsSinceLastAd = 2,
        CooldownActive = 3,
        RunTooShort = 4,
        SessionCapReached = 5,
        PlayerPaidToRemoveAds = 6,
        NotLoaded = 7,
    }

    /// <summary>The verdict, with its reason. The reason is not optional — it is how ad yield is diagnosed.</summary>
    public readonly struct AdDecision
    {
        public readonly bool Allowed;
        public readonly AdRefusal Refusal;

        private AdDecision(bool allowed, AdRefusal refusal)
        {
            Allowed = allowed;
            Refusal = refusal;
        }

        public static AdDecision Yes() => new(true, AdRefusal.None);

        public static AdDecision No(AdRefusal refusal) => new(false, refusal);
    }
}
