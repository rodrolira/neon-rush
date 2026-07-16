using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Ports;

namespace NeonRush.Application.Analytics
{
    /// <summary>
    /// Listens to the game's internal events and translates them into the analytics taxonomy.
    ///
    /// This is the whole payoff of the event-bus architecture: the wallet, the store, the run
    /// session — none of them know analytics exists. They publish their domain events exactly as
    /// they already did, and this one subscriber turns those into funnel events. Instrumenting a new
    /// system never means threading an IAnalyticsService through its constructor; it means adding a
    /// subscription here.
    ///
    /// A note on volume: CoinCollected fires many times per second and is deliberately NOT tracked
    /// per-pickup. Per-coin events would be almost all of our event volume while carrying almost no
    /// information — the run summary already contains the total. Analytics events should be
    /// decisions' worth of information, not a keylog.
    ///
    /// Pure C#. The tests assert against a recording fake that exact events fire with exact
    /// parameters — which is what keeps the dashboard trustworthy.
    /// </summary>
    public sealed class AnalyticsReporter : IDisposable
    {
        private readonly IAnalyticsService _analytics;
        private readonly List<IDisposable> _subscriptions = new();

        // One reusable dictionary per event shape would be premature here: Track is called a handful
        // of times per minute, not per frame, so a small allocation per event is irrelevant — and
        // reusing a mutable dictionary across async SDK calls is a data race waiting to happen.

        public AnalyticsReporter(IAnalyticsService analytics, IEventBus bus)
        {
            _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
            if (bus == null) throw new ArgumentNullException(nameof(bus));

            _subscriptions.Add(bus.Subscribe<RunStarted>(OnRunStarted));
            _subscriptions.Add(bus.Subscribe<RunEnded>(OnRunEnded));
            _subscriptions.Add(bus.Subscribe<RunResumed>(OnRunResumed));
            _subscriptions.Add(bus.Subscribe<CurrencyChanged>(OnCurrencyChanged));
            _subscriptions.Add(bus.Subscribe<PurchaseFailedInsufficientFunds>(OnPurchaseBlocked));
            _subscriptions.Add(bus.Subscribe<WalletTamperDetected>(OnWalletTampered));
            _subscriptions.Add(bus.Subscribe<PurchaseCompleted>(OnPurchaseCompleted));
        }

        private void OnRunStarted(RunStarted e)
        {
            _analytics.Track(AnalyticsEvents.RunStart, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.RunNumber] = e.RunNumber,
            });
        }

        private void OnRunEnded(RunEnded e)
        {
            // The single most important event in the game. Where players die, how long they last and
            // what they earn is the raw material for every difficulty and economy decision.
            _analytics.Track(AnalyticsEvents.RunEnd, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.RunNumber] = e.RunNumber,
                [AnalyticsEvents.Params.DistanceMetres] = (int)e.DistanceMetres,
                [AnalyticsEvents.Params.DurationSeconds] = (int)e.DurationSeconds,
                [AnalyticsEvents.Params.Coins] = e.CoinsCollected,
                [AnalyticsEvents.Params.Score] = e.Score,
                [AnalyticsEvents.Params.DeathCause] = e.Cause.ToString(),
            });
        }

        private void OnRunResumed(RunResumed e)
        {
            _analytics.Track(AnalyticsEvents.RunRevive, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.RunNumber] = e.RunNumber,
                [AnalyticsEvents.Params.RevivesUsed] = e.RevivesUsed,
            });
        }

        private void OnCurrencyChanged(CurrencyChanged e)
        {
            // Faucets and sinks are separate events, not one event with a sign — because the first
            // question every economy dashboard asks is "faucets vs sinks over time", and answering it
            // from a single signed event means a computed column in every single query.
            var name = e.Delta >= 0 ? AnalyticsEvents.CurrencyEarned : AnalyticsEvents.CurrencySpent;

            _analytics.Track(name, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.Currency] = e.Currency.ToString(),
                [AnalyticsEvents.Params.Amount] = Math.Abs(e.Delta),
                [AnalyticsEvents.Params.Balance] = e.Balance,
                [AnalyticsEvents.Params.Reason] = e.Reason.ToString(),
            });
        }

        private void OnPurchaseBlocked(PurchaseFailedInsufficientFunds e)
        {
            _analytics.Track(AnalyticsEvents.PurchaseBlocked, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.Currency] = e.Currency.ToString(),
                [AnalyticsEvents.Params.Price] = e.Price,
                [AnalyticsEvents.Params.Balance] = e.Balance,
                [AnalyticsEvents.Params.Shortfall] = e.Shortfall,
            });
        }

        private void OnWalletTampered(WalletTamperDetected e)
        {
            // A cheat signal, not a crash. It goes to analytics so the account can be flagged for
            // server-side review; it must never be silently swallowed.
            _analytics.Track(AnalyticsEvents.WalletTampered, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.Currency] = e.Currency.ToString(),
            });
        }

        private void OnPurchaseCompleted(PurchaseCompleted e)
        {
            _analytics.Track(AnalyticsEvents.StorePurchase, new Dictionary<string, object>
            {
                [AnalyticsEvents.Params.ItemId] = e.ItemId,
                [AnalyticsEvents.Params.RealMoney] = e.WasRealMoney,
            });
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
