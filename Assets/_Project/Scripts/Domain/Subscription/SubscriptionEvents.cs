namespace NeonRush.Domain.Subscription
{
    /// <summary>
    /// The subscription was activated or renewed. Consumed independently: the composition root turns
    /// off interstitials, the save layer flushes the paid entitlement to disk at once, analytics
    /// records the conversion, and the UI refreshes its status. A readonly struct on the zero-alloc
    /// bus, like every other gameplay event.
    /// </summary>
    public readonly struct SubscriptionActivated
    {
        /// <summary>The new expiry, so subscribers do not have to re-read the subscription.</summary>
        public readonly System.DateTime ExpiryUtc;

        public SubscriptionActivated(System.DateTime expiryUtc) => ExpiryUtc = expiryUtc;
    }
}
