using System;

namespace NeonRush.Domain.Store
{
    /// <summary>The lifecycle of the starter-pack offer for one player.</summary>
    public enum StarterPackState
    {
        /// <summary>Never shown yet; the countdown has not begun.</summary>
        NotStarted = 0,

        /// <summary>The window is running — the offer is live and counting down.</summary>
        Active = 1,

        /// <summary>The window elapsed; the offer is gone for good.</summary>
        Expired = 2,
    }

    /// <summary>
    /// The starter pack's availability window — the urgency half of the highest-converting product
    /// in the game.
    ///
    /// Pure time arithmetic over a server-anchored clock, so the "is it still available, and for how
    /// long?" decision is unit-tested without a clock, a store, or a device. Two rules shape it:
    ///
    ///  · The window is one-shot and starts when the player FIRST sees the offer, not at install. A
    ///    player who does not open the app for a week should still get their full window when they do.
    ///  · A clock that has been wound backwards can extend the window but must never expire it early:
    ///    the offer is a gift, and revoking a gift because a device's date is wrong is the wrong side
    ///    to err on. (Contrast the daily reward, which guards the clock hard because there the abuse
    ///    is the player's gain — here it is the player's loss.)
    ///
    /// Ownership is handled elsewhere (the Inventory); this type answers only the timing question.
    /// </summary>
    public sealed class StarterPackOffer
    {
        private readonly int _windowHours;

        public StarterPackOffer(int windowHours)
        {
            if (windowHours <= 0) throw new ArgumentOutOfRangeException(nameof(windowHours), "Window must be > 0 hours.");

            _windowHours = windowHours;
        }

        public int WindowHours => _windowHours;

        /// <summary>Window state given when the player first saw the offer and the current time.</summary>
        public StarterPackState StateAt(DateTime firstSeenUtc, DateTime nowUtc)
        {
            if (firstSeenUtc == DateTime.MinValue) return StarterPackState.NotStarted;

            return nowUtc >= Expiry(firstSeenUtc) ? StarterPackState.Expired : StarterPackState.Active;
        }

        /// <summary>
        /// Seconds left in the window. The full window while it has not started; zero once expired;
        /// never negative. Drives the countdown label.
        /// </summary>
        public double RemainingSeconds(DateTime firstSeenUtc, DateTime nowUtc)
        {
            if (firstSeenUtc == DateTime.MinValue) return _windowHours * 3600d;

            var remaining = (Expiry(firstSeenUtc) - nowUtc).TotalSeconds;
            return remaining > 0d ? remaining : 0d;
        }

        private DateTime Expiry(DateTime firstSeenUtc) => firstSeenUtc.AddHours(_windowHours);
    }
}
