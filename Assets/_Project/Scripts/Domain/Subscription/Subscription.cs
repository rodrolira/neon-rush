using System;

namespace NeonRush.Domain.Subscription
{
    /// <summary>
    /// A player's VIP subscription: an entitlement that is <b>time-based</b>, not owned.
    ///
    /// This is the one monetisation object that is not a boolean. A skin you have or you do not; a
    /// subscription is a clock. That single difference drives everything here: it can be renewed
    /// (stacking onto the time you have left, never truncating it — renewing early must never punish
    /// the player), it lapses on its own, and its perks come and go with it. In the shipping game the
    /// authoritative expiry is the store's (a subscription cancelled or refunded server-side must end
    /// here too); this local copy is the fast, offline-capable prediction, reconciled on sync — the
    /// same trust model as the wallet.
    ///
    /// Pure C#: the "is it active, and is a daily perk due?" logic — the part that gates real currency
    /// and ad-freedom — is unit-tested with no clock and no store.
    /// </summary>
    public sealed class Subscription
    {
        public Subscription(DateTime expiryUtc = default, DateTime lastDailyGrantUtc = default)
        {
            ExpiryUtc = expiryUtc;
            LastDailyGrantUtc = lastDailyGrantUtc;
        }

        /// <summary>When the subscription lapses. MinValue (the default) = never subscribed.</summary>
        public DateTime ExpiryUtc { get; private set; }

        /// <summary>UTC instant of the last daily-perk grant, so it is paid at most once per calendar day.</summary>
        public DateTime LastDailyGrantUtc { get; private set; }

        /// <summary>True while the subscription is live.</summary>
        public bool IsActiveAt(DateTime nowUtc) => nowUtc < ExpiryUtc;

        /// <summary>Seconds of subscription left, or zero if lapsed.</summary>
        public TimeSpan RemainingAt(DateTime nowUtc) => IsActiveAt(nowUtc) ? ExpiryUtc - nowUtc : TimeSpan.Zero;

        /// <summary>
        /// Extends the subscription by <paramref name="days"/>. Renewing while still active stacks the
        /// new period onto the remaining time; renewing after a lapse starts fresh from now. Either
        /// way the player never loses time they paid for.
        /// </summary>
        public void Extend(DateTime nowUtc, int days)
        {
            if (days <= 0) throw new ArgumentOutOfRangeException(nameof(days), "Extension must be positive.");

            var basis = ExpiryUtc > nowUtc ? ExpiryUtc : nowUtc;
            ExpiryUtc = basis.AddDays(days);
        }

        /// <summary>True when the daily perk is due: the sub is active and nothing was granted yet on this UTC day.</summary>
        public bool DailyGrantAvailableAt(DateTime nowUtc)
        {
            if (!IsActiveAt(nowUtc)) return false;

            // Calendar-day comparison in UTC, matching the daily reward. A player in any timezone gets
            // exactly one grant per UTC day; the server is the tiebreaker in the shipping game.
            return LastDailyGrantUtc.Date < nowUtc.Date;
        }

        /// <summary>Records that the daily perk was paid, stamping the trusted clock. Caller must credit the currency.</summary>
        public void MarkDailyGranted(DateTime nowUtc) => LastDailyGrantUtc = nowUtc;
    }
}
