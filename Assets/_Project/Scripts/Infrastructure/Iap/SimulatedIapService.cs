using System;
using System.Collections.Generic;
using NeonRush.Domain.Ports;
using UnityEngine;

namespace NeonRush.Infrastructure.Iap
{
    /// <summary>
    /// A billing service that takes no money. Development and QA only.
    ///
    /// A real purchase requires a signed build, a configured Play Console / App Store Connect
    /// product, a test account, and a minute of tapping through a payment sheet — per attempt. That
    /// friction is why the purchase pipeline is the least-exercised code in most mobile games, and
    /// why it so often ships broken.
    ///
    /// The failure dials are the point. The bugs that cost real money are never in the happy path:
    /// they are in "the player cancelled", "the receipt was rejected", "the store never initialised",
    /// "they already own it". Set <see cref="NextStatus"/> and walk the whole store.
    /// </summary>
    public sealed class SimulatedIapService : IIapService
    {
        private readonly Dictionary<string, string> _prices = new();

        public SimulatedIapService()
        {
            // Localised-looking placeholders, so the UI is laid out against realistic strings rather
            // than "$1" — a price field sized for "$1" explodes when it meets "1 234,56 Kč".
            _prices["com.mooncatstudio.neonrush.gems_100"] = "0,99 €";
            _prices["com.mooncatstudio.neonrush.gems_500"] = "4,49 €";
            _prices["com.mooncatstudio.neonrush.gems_1200"] = "9,99 €";
            _prices["com.mooncatstudio.neonrush.gems_2500"] = "19,99 €";
            _prices["com.mooncatstudio.neonrush.gems_5000"] = "39,99 €";
            _prices["com.mooncatstudio.neonrush.starter"] = "2,99 €";
            _prices["com.mooncatstudio.neonrush.noads"] = "3,99 €";
        }

        /// <summary>What the next purchase attempt reports. Turn this dial and exercise every path.</summary>
        public IapStatus NextStatus { get; set; } = IapStatus.Purchased;

        public bool IsInitialised { get; set; } = true;

        public string GetLocalisedPrice(string productId) =>
            _prices.TryGetValue(productId, out var price) ? price : null;

        public void Purchase(string productId, Action<IapResult> onFinished)
        {
            Debug.Log($"[SimulatedIap] Purchase '{productId}' -> {NextStatus}");

            if (NextStatus != IapStatus.Purchased)
            {
                onFinished?.Invoke(new IapResult(NextStatus));
                return;
            }

            var purchase = new IapPurchase(
                productId,
                receipt: $"SIMULATED_RECEIPT::{productId}",
                transactionId: Guid.NewGuid().ToString("N"));

            onFinished?.Invoke(new IapResult(IapStatus.Purchased, purchase));
        }

        public void RestorePurchases(Action<bool> onFinished) => onFinished?.Invoke(true);
    }
}
