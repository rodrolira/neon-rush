using System;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Ports;

namespace NeonRush.Domain.Retention
{
    /// <summary>A daily reward was claimed. Analytics and the UI listen.</summary>
    public readonly struct DailyRewardClaimed
    {
        /// <summary>1-based day within the streak cycle (1..7).</summary>
        public readonly int StreakDay;

        public readonly int CoinsGranted;
        public readonly int GemsGranted;

        public DailyRewardClaimed(int streakDay, int coinsGranted, int gemsGranted)
        {
            StreakDay = streakDay;
            CoinsGranted = coinsGranted;
            GemsGranted = gemsGranted;
        }
    }

    /// <summary>Why a claim was refused. Reported, so retention problems are diagnosable.</summary>
    public enum ClaimRefusal
    {
        None = 0,

        /// <summary>Already claimed today. Come back tomorrow.</summary>
        AlreadyClaimedToday = 1,

        /// <summary>
        /// The trusted clock reads EARLIER than the last claim. Time cannot run backwards; either the
        /// device clock was rolled back (tamper) or a server resync corrected a fast clock. Either
        /// way, granting would mint currency, so the claim waits until time catches up.
        /// </summary>
        TimeInconsistent = 2,
    }

    /// <summary>
    /// The daily login reward and its streak.
    ///
    /// The retention mechanics, stated plainly: the reward gives a lapsing player a reason to open
    /// the app today, and the streak gives them a reason to come back TOMORROW — because tomorrow's
    /// reward is bigger, and missing a day forfeits the progression. Day 7 pays premium currency,
    /// which is what makes the cycle worth completing rather than merely worth visiting.
    ///
    /// Every timing rule here leans on <see cref="IClock"/> (see ARCHITECTURE.md §6 — time is an
    /// attack surface). The specific decisions:
    ///
    ///  · <b>Days are UTC calendar days of the trusted clock</b>, not local days and not rolling
    ///    24-hour windows. Local days would let a player claim twice by changing their timezone;
    ///    rolling windows quietly drift the claim time later every day until the player misses one
    ///    through no fault of their own — a streak lost to arithmetic, which they experience as theft.
    ///
    ///  · <b>A missed day resets the streak; the claim itself never punishes.</b> Opening the app on
    ///    day 9 after claiming on day 7 restarts at day 1. Harsh-but-legible beats generous-but-mushy:
    ///    the entire motivational power of a streak comes from it being possible to lose.
    ///
    ///  · <b>Time running backwards refuses the claim but keeps the streak.</b> A rolled-back device
    ///    clock must not mint rewards, but an honest player whose clock was corrected by a server
    ///    resync must not lose their streak over our bookkeeping. Refuse, report, wait.
    ///
    /// Pure C#. Every scenario — midnight boundaries, missed days, clock rollback — is a unit test
    /// with a FakeClock, not a QA ticket with a hand-adjusted phone.
    /// </summary>
    public sealed class DailyRewardService
    {
        /// <summary>Days in one streak cycle. After day 7, the cycle repeats at day 1 (streak intact).</summary>
        public const int CycleLength = 7;

        // Escalating cycle. Coins ramp through the week; day 7 pays gems — the premium anchor that
        // makes completing the cycle feel meaningfully different from merely visiting.
        private static readonly (int coins, int gems)[] Rewards =
        {
            (100, 0),  // day 1
            (150, 0),  // day 2
            (200, 0),  // day 3
            (300, 0),  // day 4
            (400, 0),  // day 5
            (600, 0),  // day 6
            (500, 25), // day 7
        };

        private readonly Wallet _wallet;
        private readonly IClock _clock;
        private readonly IEventBus _bus;

        /// <summary>UTC instant of the last successful claim. MinValue = never claimed.</summary>
        public DateTime LastClaimUtc { get; private set; }

        /// <summary>Consecutive days claimed, across cycles. 0 = never claimed.</summary>
        public int StreakDays { get; private set; }

        public DailyRewardService(Wallet wallet, IClock clock, IEventBus bus,
            DateTime lastClaimUtc = default, int streakDays = 0)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));

            LastClaimUtc = lastClaimUtc == default ? DateTime.MinValue : lastClaimUtc;
            StreakDays = Math.Max(0, streakDays);
        }

        /// <summary>1-based day (1..7) the NEXT successful claim will pay out.</summary>
        public int NextStreakDay => WouldContinueStreak ? StreakDays % CycleLength + 1 : 1;

        /// <summary>True when claiming now would extend the streak rather than restart it.</summary>
        private bool WouldContinueStreak
        {
            get
            {
                if (StreakDays == 0) return false;

                var daysSince = DaysBetween(LastClaimUtc, _clock.UtcNow);
                return daysSince == 1;
            }
        }

        /// <summary>The reward the next claim will pay. The UI shows this before the player claims.</summary>
        public (int coins, int gems) NextReward => Rewards[NextStreakDay - 1];

        /// <summary>Whether a claim is available right now, and if not, why.</summary>
        public ClaimRefusal Availability()
        {
            var now = _clock.UtcNow;

            if (LastClaimUtc == DateTime.MinValue) return ClaimRefusal.None;

            // Time cannot run backwards. A now earlier than the last claim means a rolled-back
            // device clock or a resync correction; grant nothing until time catches up. The streak
            // is deliberately untouched — an honest player must not lose it to our bookkeeping.
            if (now < LastClaimUtc) return ClaimRefusal.TimeInconsistent;

            if (DaysBetween(LastClaimUtc, now) == 0) return ClaimRefusal.AlreadyClaimedToday;

            return ClaimRefusal.None;
        }

        /// <summary>
        /// Claims today's reward, if available. Returns the refusal reason otherwise.
        /// </summary>
        public ClaimRefusal TryClaim(out DailyRewardClaimed claimed)
        {
            claimed = default;

            var refusal = Availability();
            if (refusal != ClaimRefusal.None) return refusal;

            var now = _clock.UtcNow;

            // Extend or restart the streak. More than one calendar day since the last claim means a
            // day was missed and the streak restarts — see the class remarks for why no mercy window.
            StreakDays = WouldContinueStreak ? StreakDays + 1 : 1;
            LastClaimUtc = now;

            var day = (StreakDays - 1) % CycleLength + 1;
            var (coins, gems) = Rewards[day - 1];

            if (coins > 0) _wallet.Credit(CurrencyType.Coins, coins, TransactionReason.DailyReward);
            if (gems > 0) _wallet.Credit(CurrencyType.Gems, gems, TransactionReason.DailyReward);

            claimed = new DailyRewardClaimed(day, coins, gems);
            _bus.Publish(claimed);

            return ClaimRefusal.None;
        }

        /// <summary>
        /// Whole UTC calendar days between two instants. Calendar days, not 24-hour spans: a claim
        /// at 23:59 and another at 00:01 are different days and both count, which is exactly what a
        /// player expects "daily" to mean.
        /// </summary>
        private static int DaysBetween(DateTime earlier, DateTime later) =>
            (int)(later.Date - earlier.Date).TotalDays;
    }
}
