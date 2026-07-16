namespace NeonRush.Application.Analytics
{
    /// <summary>
    /// The complete analytics taxonomy: every event name and parameter key this game ever sends.
    ///
    /// This file is the schema, and it is deliberately the ONLY place event names exist. A name used
    /// at a call site ("run_end" here, "runEnded" there) never survives contact with a second
    /// engineer; the dashboard then contains two half-populated events and every funnel built on
    /// them silently undercounts. Centralising the names makes the schema code-reviewed, greppable,
    /// and renameable as a compile-checked refactor.
    ///
    /// Naming convention: snake_case, verb-last (`run_end`, not `end_run`), matching Firebase's own
    /// style so custom and automatic events sort together in the console.
    /// </summary>
    public static class AnalyticsEvents
    {
        // --- Core loop -------------------------------------------------------------------
        public const string RunStart = "run_start";
        public const string RunEnd = "run_end";
        public const string RunRevive = "run_revive";

        // --- Economy ---------------------------------------------------------------------
        public const string CurrencyEarned = "currency_earned";
        public const string CurrencySpent = "currency_spent";

        /// <summary>
        /// The single most commercially valuable event in the game: the exact moment a player wanted
        /// something they could not afford. Offer targeting is built on this.
        /// </summary>
        public const string PurchaseBlocked = "purchase_blocked_insufficient_funds";

        public const string WalletTampered = "wallet_tamper_detected";

        // --- Store -----------------------------------------------------------------------
        public const string StorePurchase = "store_purchase";

        // --- Ads -------------------------------------------------------------------------
        public const string AdShown = "ad_shown";

        // --- Parameter keys ----------------------------------------------------------------
        public static class Params
        {
            public const string RunNumber = "run_number";
            public const string DistanceMetres = "distance_m";
            public const string DurationSeconds = "duration_s";
            public const string Coins = "coins";
            public const string Score = "score";
            public const string DeathCause = "death_cause";
            public const string RevivesUsed = "revives_used";

            public const string Currency = "currency";
            public const string Amount = "amount";
            public const string Balance = "balance";
            public const string Reason = "reason";
            public const string Shortfall = "shortfall";
            public const string Price = "price";

            public const string ItemId = "item_id";
            public const string RealMoney = "real_money";

            public const string Placement = "placement";
        }

        // --- User property names -----------------------------------------------------------
        public static class UserProps
        {
            /// <summary>Lifetime run count, bucketed ("1-5", "6-20", ...) so it segments instead of exploding cardinality.</summary>
            public const string RunsBucket = "runs_bucket";

            public const string BestScore = "best_score";
        }

        /// <summary>Buckets a lifetime run count for the <see cref="UserProps.RunsBucket"/> property.</summary>
        public static string BucketRuns(int totalRuns) => totalRuns switch
        {
            <= 5 => "1-5",
            <= 20 => "6-20",
            <= 100 => "21-100",
            <= 500 => "101-500",
            _ => "500+",
        };
    }
}
