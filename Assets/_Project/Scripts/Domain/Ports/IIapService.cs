using System;

namespace NeonRush.Domain.Ports
{
    /// <summary>How a real-money purchase ended.</summary>
    public enum IapStatus
    {
        /// <summary>
        /// The platform store says the player paid.
        ///
        /// <b>This is a claim, not a fact.</b> It has NOT been validated. Nothing may be granted on
        /// the strength of this value alone — the receipt must go to the server first. See
        /// <see cref="IReceiptValidator"/>.
        /// </summary>
        Purchased = 0,

        /// <summary>The player backed out. Not an error; do not report it as one.</summary>
        Cancelled = 1,

        /// <summary>The payment was declined, or the store failed.</summary>
        Failed = 2,

        /// <summary>The store is not initialised (no network at launch, or a misconfigured product).</summary>
        Unavailable = 3,

        /// <summary>
        /// The player already owns this and it is not consumable.
        /// This is the "restore purchases" path, and on iOS it is a legal requirement, not a nicety.
        /// </summary>
        AlreadyOwned = 4,
    }

    /// <summary>A completed platform purchase, as reported by the store. Untrusted until validated.</summary>
    public readonly struct IapPurchase
    {
        public readonly string ProductId;

        /// <summary>The opaque, signed receipt from Google Play or the App Store. This is what the server validates.</summary>
        public readonly string Receipt;

        /// <summary>The store's transaction id. Used server-side to detect a replayed receipt.</summary>
        public readonly string TransactionId;

        public IapPurchase(string productId, string receipt, string transactionId)
        {
            ProductId = productId;
            Receipt = receipt;
            TransactionId = transactionId;
        }
    }

    /// <summary>Result of a purchase attempt.</summary>
    public readonly struct IapResult
    {
        public readonly IapStatus Status;
        public readonly IapPurchase Purchase;
        public readonly string Error;

        public IapResult(IapStatus status, IapPurchase purchase = default, string error = null)
        {
            Status = status;
            Purchase = purchase;
            Error = error;
        }

        public bool IsPurchased => Status == IapStatus.Purchased;
    }

    /// <summary>
    /// The platform billing layer (Google Play Billing / StoreKit, via Unity Purchasing).
    ///
    /// Behind a port for the usual reasons, plus one specific to IAP: a real purchase costs real
    /// money and can only be made on a signed build with a configured store account. Without this
    /// seam, the entire purchase→validate→grant pipeline would be untestable, and it is the one
    /// pipeline in the game where a bug costs the player actual money and costs you a chargeback.
    /// </summary>
    public interface IIapService
    {
        /// <summary>True once the store has connected and the product catalogue is known.</summary>
        bool IsInitialised { get; }

        /// <summary>Localised price string for a product ("4,99 €"), or null if unknown.</summary>
        /// <remarks>
        /// Always show the store's own localised string. Never format a price yourself from a number:
        /// you will get the currency, the separator, or the tax-inclusive rounding wrong for some
        /// country, and the price the player sees will not match what they are charged.
        /// </remarks>
        string GetLocalisedPrice(string productId);

        /// <summary>Starts a purchase. The callback fires exactly once.</summary>
        void Purchase(string productId, Action<IapResult> onFinished);

        /// <summary>
        /// Restores previously-bought non-consumables.
        ///
        /// Mandatory on iOS — Apple rejects apps without a visible restore path — and necessary
        /// everywhere for a player who reinstalls or changes device.
        /// </summary>
        void RestorePurchases(Action<bool> onFinished);
    }

    /// <summary>Verdict on a receipt.</summary>
    public readonly struct ValidationResult
    {
        /// <summary>True only when the receipt was verified as genuine and unused.</summary>
        public readonly bool IsValid;

        /// <summary>Why it was rejected. Reported to analytics — a spike here is an attack.</summary>
        public readonly string Reason;

        private ValidationResult(bool valid, string reason)
        {
            IsValid = valid;
            Reason = reason;
        }

        public static ValidationResult Valid() => new(true, null);

        public static ValidationResult Invalid(string reason) => new(false, reason);
    }

    /// <summary>
    /// Verifies that a receipt is genuine before anything is granted.
    ///
    /// <b>This is the most security-critical interface in the game, and the rule has no exceptions:
    /// a client may never decide that a purchase is real.</b>
    ///
    /// The naive implementation — the store SDK calls back with "purchased", so credit 5,000 gems —
    /// is drained within days of launch by tools that do nothing more sophisticated than replay a
    /// captured receipt, or hook the callback and invoke it directly. The client is running on the
    /// attacker's machine; anything it decides, the attacker decides.
    ///
    /// The only correct flow is:
    ///
    ///   1. The store hands the client a signed receipt.
    ///   2. The client sends the receipt to your backend (a Firebase Cloud Function).
    ///   3. The backend calls Google Play Developer API / Apple's verifyReceipt with its OWN
    ///      credentials — which the client does not have and cannot extract.
    ///   4. The backend checks the transaction id against its record of already-redeemed receipts,
    ///      to defeat replay.
    ///   5. The backend credits the currency in Firestore and tells the client what it now owns.
    ///
    /// The currency is created on the server. The client is only ever told about it.
    /// </summary>
    public interface IReceiptValidator
    {
        /// <summary>
        /// Validates a receipt and, if genuine, grants the entitlement server-side.
        ///
        /// Callers MUST NOT grant anything unless <see cref="ValidationResult.IsValid"/> is true.
        /// </summary>
        void Validate(IapPurchase purchase, Action<ValidationResult> onFinished);
    }
}
