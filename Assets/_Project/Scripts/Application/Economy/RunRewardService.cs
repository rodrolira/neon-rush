using System;
using NeonRush.Application.Events;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;

namespace NeonRush.Application.Economy
{
    /// <summary>
    /// Turns a finished run into currency, and owns the "double your coins" rewarded-ad offer.
    ///
    /// Two design decisions here carry real money, so they are worth defending explicitly.
    ///
    /// <b>1. Coins are banked when the run ends, not as they are picked up.</b>
    /// Crediting each coin the instant it is collected sounds simpler, but it makes the doubling
    /// offer incoherent: by the time you show it, the coins are already spent-able, and "double" has
    /// to mean "retroactively credit the same amount again" anyway. Banking once, at the end, keeps
    /// a run's earnings as a single auditable transaction, which is also what the server needs to
    /// validate a run's plausibility.
    ///
    /// <b>2. The base reward is credited IMMEDIATELY on death, before the offer is shown.</b>
    /// This is the important one. The tempting design is to hold the coins hostage — show the ad
    /// offer and only credit anything once the player chooses. Never do that. If the player closes
    /// the app, loses signal, or the ad fails to load, they lose coins they legitimately earned, and
    /// that is the kind of thing players correctly perceive as theft. Here, the run's coins are
    /// always theirs. The ad adds a second, equal credit on top. The offer is a bonus, never a
    /// ransom — and it converts better that way, because the player is being offered a gift rather
    /// than being extorted for what they already earned.
    ///
    /// Pure C#. No Unity, no ad SDK — the ad is behind a port, so this is fully unit-testable.
    /// </summary>
    public sealed class RunRewardService : IDisposable
    {
        private readonly Wallet _wallet;
        private readonly IEventBus _bus;
        private readonly IDisposable _subscription;

        /// <summary>Coins earned in the run that just finished, and already credited once.</summary>
        private int _lastRunCoins;

        /// <summary>Guards against the doubling being claimed twice for the same run.</summary>
        private bool _doubleClaimed;

        public RunRewardService(Wallet wallet, IEventBus bus)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            _subscription = _bus.Subscribe<RunEnded>(OnRunEnded);
        }

        /// <summary>
        /// True when a "double your coins" offer is worth showing.
        ///
        /// Note the <c>&gt; 0</c>: offering to double zero coins is an ad impression that insults the
        /// player and earns nothing. A player who died instantly with no coins should just be allowed
        /// to retry.
        /// </summary>
        public bool CanClaimDouble => !_doubleClaimed && _lastRunCoins > 0;

        /// <summary>Coins earned in the last run. This is the amount the doubling offer would add.</summary>
        public int LastRunCoins => _lastRunCoins;

        private void OnRunEnded(RunEnded e)
        {
            _lastRunCoins = e.CoinsCollected;
            _doubleClaimed = false;

            if (_lastRunCoins <= 0) return;

            // Credited unconditionally, right now. The player earned these; nothing they do or fail
            // to do next can take them away.
            _wallet.Credit(CurrencyType.Coins, _lastRunCoins, TransactionReason.RunReward);
        }

        /// <summary>
        /// Credits the run's coins a second time. Call this ONLY after a rewarded ad has actually
        /// completed — never on ad-start, never on ad-failure.
        /// </summary>
        /// <returns>Coins added, or 0 if there was nothing to double or it was already claimed.</returns>
        public int ClaimDouble()
        {
            if (!CanClaimDouble) return 0;

            _doubleClaimed = true;

            // A distinct reason, not a second RunReward. This is what lets the economy dashboard
            // answer "how much of our coin inflation comes from rewarded ads?" — which is the number
            // that decides whether the ad is priced correctly against the coin sinks.
            return _wallet.Credit(CurrencyType.Coins, _lastRunCoins, TransactionReason.RunRewardDoubled);
        }

        public void Dispose() => _subscription.Dispose();
    }
}
