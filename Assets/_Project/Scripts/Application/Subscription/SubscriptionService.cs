using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Ports;
using Sub = NeonRush.Domain.Subscription.Subscription;

namespace NeonRush.Application.Subscription
{
    /// <summary>
    /// Runs the VIP subscription: turns a purchase into an active period, and pays out its perks —
    /// a coin multiplier on every run, a daily gem stipend, and ad-freedom (handled by the composition
    /// root, which owns the ad services).
    ///
    /// The purchase itself is the ordinary store flow: VIP is the catalogue's <c>vip_monthly</c>, so
    /// it gets the same real-money, receipt-validated path as everything else, and this service simply
    /// reacts to the resulting <see cref="PurchaseCompleted"/>. That keeps the one genuinely different
    /// thing about a subscription — that it is a clock, not a possession — isolated in the Domain's
    /// <see cref="Sub"/>, while the money plumbing stays shared.
    ///
    /// Every perk credit carries <see cref="TransactionReason.SubscriptionGrant"/>, so the economy
    /// dashboard can measure exactly how much currency VIP injects — the number that says whether the
    /// subscription is priced right against its perks.
    /// </summary>
    public sealed class SubscriptionService : IDisposable
    {
        /// <summary>The catalogue id of the VIP subscription product.</summary>
        public const string ProductItemId = "vip_monthly";

        private readonly Sub _subscription;
        private readonly Wallet _wallet;
        private readonly IClock _clock;
        private readonly SubscriptionConfig _config;
        private readonly IEventBus _bus;
        private readonly List<IDisposable> _subscriptions = new();

        public SubscriptionService(Sub subscription, Wallet wallet, IClock clock, SubscriptionConfig config, IEventBus bus)
        {
            _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            _subscriptions.Add(bus.Subscribe<PurchaseCompleted>(OnPurchaseCompleted));

            // The coin multiplier is paid as a separate credit on RunEnded — additive, so it never
            // touches RunRewardService and shows up on the dashboard as its own faucet.
            _subscriptions.Add(bus.Subscribe<RunEnded>(OnRunEnded));
        }

        public bool Enabled => _config.Enabled;

        public bool IsActive => _subscription.IsActiveAt(_clock.UtcNow);

        public TimeSpan Remaining => _subscription.RemainingAt(_clock.UtcNow);

        public int DailyGems => _config.DailyGems;

        public float CoinMultiplier => _config.CoinMultiplier;

        /// <summary>True when today's gem stipend is claimable.</summary>
        public bool DailyGemsAvailable => IsActive && _subscription.DailyGrantAvailableAt(_clock.UtcNow);

        // Persistence surface.
        public DateTime ExpiryUtc => _subscription.ExpiryUtc;
        public DateTime LastDailyGrantUtc => _subscription.LastDailyGrantUtc;

        private void OnPurchaseCompleted(PurchaseCompleted e)
        {
            if (e.ItemId != ProductItemId) return;
            if (!_config.Enabled) return;

            _subscription.Extend(_clock.UtcNow, _config.DurationDays);
            _bus.Publish(new NeonRush.Domain.Subscription.SubscriptionActivated(_subscription.ExpiryUtc));
        }

        private void OnRunEnded(RunEnded e)
        {
            if (!IsActive || _config.CoinMultiplier <= 1f || e.CoinsCollected <= 0) return;

            var bonus = (int)Math.Round(e.CoinsCollected * (_config.CoinMultiplier - 1f));
            if (bonus <= 0) return;

            _wallet.Credit(CurrencyType.Coins, bonus, TransactionReason.SubscriptionGrant);
        }

        /// <summary>
        /// Claims today's gem stipend, if it is due. Returns the gems granted, or 0 if it was not
        /// available (lapsed, or already taken today). Safe to call repeatedly — it can never pay
        /// twice in a UTC day.
        /// </summary>
        public int ClaimDailyGems()
        {
            if (!DailyGemsAvailable) return 0;

            _subscription.MarkDailyGranted(_clock.UtcNow);
            return _wallet.Credit(CurrencyType.Gems, _config.DailyGems, TransactionReason.SubscriptionGrant);
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
