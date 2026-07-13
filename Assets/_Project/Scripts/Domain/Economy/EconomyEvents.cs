namespace NeonRush.Domain.Economy
{
    /// <summary>A balance changed. The HUD, the store and the analytics funnel all listen for this.</summary>
    public readonly struct CurrencyChanged
    {
        public readonly CurrencyType Currency;

        /// <summary>Signed change. Positive = credited, negative = debited.</summary>
        public readonly int Delta;

        /// <summary>Balance after the change.</summary>
        public readonly int Balance;

        public readonly TransactionReason Reason;

        public CurrencyChanged(CurrencyType currency, int delta, int balance, TransactionReason reason)
        {
            Currency = currency;
            Delta = delta;
            Balance = balance;
            Reason = reason;
        }
    }

    /// <summary>
    /// A purchase was refused because the player could not afford it.
    ///
    /// This is one of the most valuable events in the game commercially, and it is routinely not
    /// tracked. It is the exact moment a player wanted something and could not have it — which is
    /// the only moment at which an offer for that thing is not an interruption but a solution.
    /// Every "insufficient funds" is a qualified lead.
    /// </summary>
    public readonly struct PurchaseFailedInsufficientFunds
    {
        public readonly CurrencyType Currency;

        /// <summary>What the item cost.</summary>
        public readonly int Price;

        /// <summary>What the player had.</summary>
        public readonly int Balance;

        /// <summary>How much they were short. This is the number an offer should be sized against.</summary>
        public readonly int Shortfall;

        public PurchaseFailedInsufficientFunds(CurrencyType currency, int price, int balance)
        {
            Currency = currency;
            Price = price;
            Balance = balance;
            Shortfall = price - balance;
        }
    }

    /// <summary>
    /// The wallet detected that its memory was modified externally.
    ///
    /// Never swallow this. It must reach analytics (so the account can be flagged for server-side
    /// review) and it must force a resynchronisation from the server, because the local balance can
    /// no longer be trusted.
    /// </summary>
    public readonly struct WalletTamperDetected
    {
        public readonly CurrencyType Currency;

        public WalletTamperDetected(CurrencyType currency) => Currency = currency;
    }
}
