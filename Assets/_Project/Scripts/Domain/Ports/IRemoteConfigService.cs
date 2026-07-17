using System;

namespace NeonRush.Domain.Ports
{
    /// <summary>
    /// Remotely-tunable values, delivered by the backend (Firebase Remote Config in production).
    ///
    /// This is the mechanism behind the brief's "no hardcoded values" requirement: shop prices, ad
    /// frequency, difficulty, drop rates and event windows are all *data*, changeable without a
    /// store submission. LiveOps lives or dies on this — the ability to fix a too-aggressive ad
    /// cadence or a too-hard difficulty curve for a struggling cohort in minutes, not in a two-week
    /// review cycle.
    ///
    /// Two rules govern every use of this port, and both are load-bearing:
    ///
    ///  1. <b>Every key has a compiled-in default that ships a complete, balanced game.</b> A player
    ///     who launches offline on day one — no network, no fetch — must get the full experience.
    ///     Remote Config tunes the game; it does not constitute it. A missing key is never an error.
    ///
    ///  2. <b>The fetch is asynchronous and never blocks the boot.</b> The game starts instantly on
    ///     defaults and swaps in remote values when (if) they arrive. Blocking the first frame on a
    ///     network round trip is how a game gets a reputation for a slow, janky launch.
    ///
    /// The values themselves are still untrusted after they arrive — see GameConfigService, which
    /// clamps every one to a safe range so a malformed or hostile push cannot brick the game.
    /// </summary>
    public interface IRemoteConfigService
    {
        /// <summary>
        /// True once a fetch has completed and remote values (if any) are live. While false, every
        /// getter returns its default — which is a fully playable configuration, not a placeholder.
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Fetches the latest config in the background. Cheap to call; a fetch already in flight is
        /// coalesced. The callback fires with true on a successful fetch, false on failure — but a
        /// failure is not fatal, because the defaults are already a complete game.
        /// </summary>
        void Fetch(Action<bool> onComplete);

        /// <summary>Returns the remote value for <paramref name="key"/>, or <paramref name="defaultValue"/> if absent.</summary>
        int GetInt(string key, int defaultValue);

        float GetFloat(string key, float defaultValue);

        bool GetBool(string key, bool defaultValue);

        string GetString(string key, string defaultValue);
    }

    /// <summary>
    /// The Remote Config key names, in one place.
    ///
    /// Stringly-typed config keys scattered through the codebase are a well-known source of silent
    /// bugs: a typo in a key name does not fail to compile, it just silently returns the default and
    /// your remote change appears to do nothing. Centralising them here means the backend console and
    /// the client agree on exactly one spelling, and a rename is a compile-checked refactor.
    /// </summary>
    public static class RemoteConfigKeys
    {
        // --- Difficulty / run tuning ---
        public const string BaseSpeed = "run_base_speed";
        public const string MaxSpeed = "run_max_speed";
        public const string SpeedGainPerMetre = "run_speed_gain_per_metre";
        public const string SafeStartDistance = "run_safe_start_distance";
        public const string ScorePerCoin = "run_score_per_coin";

        // --- Ads ---
        public const string AdGraceRuns = "ad_grace_runs";
        public const string AdRunsBetween = "ad_runs_between_interstitials";
        public const string AdSecondsBetween = "ad_seconds_between_interstitials";
        public const string AdMinRunSeconds = "ad_min_run_seconds_for_interstitial";
        public const string AdMaxPerSession = "ad_max_interstitials_per_session";

        // --- Economy ---
        public const string DoubleCoinsEnabled = "econ_double_coins_enabled";
        public const string ReviveEnabled = "econ_revive_enabled";

        // --- Battle pass (pacing scalars; the reward ladder is compiled content) ---
        public const string BattlePassXpPerMetre = "bp_xp_per_metre";
        public const string BattlePassXpPerCoin = "bp_xp_per_coin";
        public const string BattlePassPremiumGemPrice = "bp_premium_gem_price";

        // --- Starter pack ---
        public const string StarterPackEnabled = "starter_pack_enabled";
        public const string StarterPackWindowHours = "starter_pack_window_hours";

        // --- VIP subscription (perks; the price is owned by the platform store) ---
        public const string VipEnabled = "vip_enabled";
        public const string VipDurationDays = "vip_duration_days";
        public const string VipDailyGems = "vip_daily_gems";
        public const string VipCoinMultiplier = "vip_coin_multiplier";

        // --- Store prices (currency items only; real-money prices come from the platform store) ---
        // Convention: "price_" + itemId. Kept as a helper so a new item does not need a new constant.
        public static string PriceKeyFor(string itemId) => "price_" + itemId;
    }
}
