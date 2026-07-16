using System;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Retention;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the daily reward and its streak.
    ///
    /// Every one of these scenarios — midnight boundaries, missed days, rolled-back clocks, timezone
    /// hops — would be a QA ticket requiring a phone with a hand-adjusted date. With IClock they are
    /// millisecond unit tests, which is precisely why the clock was made injectable.
    /// </summary>
    [TestFixture]
    public sealed class DailyRewardServiceTests
    {
        private EventBus _bus;
        private Wallet _wallet;
        private FakeClock _clock;
        private DailyRewardService _rewards;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _wallet = new Wallet(_bus);
            _clock = new FakeClock { UtcNow = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc) };
            _rewards = new DailyRewardService(_wallet, _clock, _bus);
        }

        [TearDown]
        public void TearDown() => _bus.Dispose();

        private ClaimRefusal Claim(out DailyRewardClaimed claimed) => _rewards.TryClaim(out claimed);

        private void NextDay(int days = 1) => _clock.UtcNow = _clock.UtcNow.AddDays(days);

        [Test]
        public void FirstClaim_StartsTheStreakAndPaysDayOne()
        {
            var refusal = Claim(out var claimed);

            Assert.That(refusal, Is.EqualTo(ClaimRefusal.None));
            Assert.That(claimed.StreakDay, Is.EqualTo(1));
            Assert.That(_rewards.StreakDays, Is.EqualTo(1));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(claimed.CoinsGranted));
        }

        [Test]
        public void ClaimingTwiceInOneDay_IsRefused()
        {
            Claim(out _);
            var balance = _wallet.Balance(CurrencyType.Coins);

            // Hours later, same UTC day.
            _clock.UtcNow = _clock.UtcNow.AddHours(6);

            var refusal = Claim(out _);

            Assert.That(refusal, Is.EqualTo(ClaimRefusal.AlreadyClaimedToday));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(balance), "Nothing may be paid twice.");
        }

        [Test]
        public void ConsecutiveDays_ExtendTheStreakWithEscalatingRewards()
        {
            Claim(out var day1);
            NextDay();
            Claim(out var day2);
            NextDay();
            Claim(out var day3);

            Assert.That(_rewards.StreakDays, Is.EqualTo(3));
            Assert.That(day2.StreakDay, Is.EqualTo(2));
            Assert.That(day3.StreakDay, Is.EqualTo(3));
            Assert.That(day2.CoinsGranted, Is.GreaterThan(day1.CoinsGranted),
                "Each day must pay more than the last, or there is no reason to come back tomorrow.");
            Assert.That(day3.CoinsGranted, Is.GreaterThan(day2.CoinsGranted));
        }

        [Test]
        public void MissingADay_ResetsTheStreakToDayOne()
        {
            Claim(out _);
            NextDay();
            Claim(out _);
            Assert.That(_rewards.StreakDays, Is.EqualTo(2));

            NextDay(2); // skipped a day

            Claim(out var claimed);

            Assert.That(claimed.StreakDay, Is.EqualTo(1),
                "A missed day restarts the cycle — the streak's power comes from being losable.");
            Assert.That(_rewards.StreakDays, Is.EqualTo(1));
        }

        [Test]
        public void DaySeven_PaysGems()
        {
            // The premium anchor that makes completing the cycle different from merely visiting.
            DailyRewardClaimed last = default;

            for (var day = 0; day < 7; day++)
            {
                Claim(out last);
                NextDay();
            }

            Assert.That(last.StreakDay, Is.EqualTo(7));
            Assert.That(last.GemsGranted, Is.GreaterThan(0), "Day 7 must pay premium currency.");
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(last.GemsGranted));
        }

        [Test]
        public void AfterDaySeven_TheCycleRepeatsButTheStreakKeepsCounting()
        {
            for (var day = 0; day < 8; day++)
            {
                Claim(out var claimed);

                if (day == 7)
                {
                    Assert.That(claimed.StreakDay, Is.EqualTo(1), "Day 8 pays the day-1 reward again...");
                }

                NextDay();
            }

            Assert.That(_rewards.StreakDays, Is.EqualTo(8), "...but the streak itself is unbroken.");
        }

        [Test]
        public void MidnightBoundary_CountsAsANewDay()
        {
            // Calendar days, not 24-hour spans: 23:59 and 00:01 are different days and both claimable.
            _clock.UtcNow = new DateTime(2026, 7, 10, 23, 59, 0, DateTimeKind.Utc);
            Claim(out _);

            _clock.UtcNow = new DateTime(2026, 7, 11, 0, 1, 0, DateTimeKind.Utc);

            var refusal = Claim(out var claimed);

            Assert.That(refusal, Is.EqualTo(ClaimRefusal.None),
                "Two minutes apart across midnight is two calendar days; the claim must be allowed.");
            Assert.That(claimed.StreakDay, Is.EqualTo(2), "And it must CONTINUE the streak.");
        }

        [Test]
        public void ARolledBackClock_IsRefusedButTheStreakSurvives()
        {
            // The tamper case: claim, roll the clock back a day, try to claim "yesterday" again.
            Claim(out _);
            var balance = _wallet.Balance(CurrencyType.Coins);

            _clock.UtcNow = _clock.UtcNow.AddDays(-1);

            var refusal = Claim(out _);

            Assert.That(refusal, Is.EqualTo(ClaimRefusal.TimeInconsistent),
                "Time running backwards must never mint a reward.");
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(balance));
            Assert.That(_rewards.StreakDays, Is.EqualTo(1),
                "But the streak is untouched — an honest resync correction must not cost the player.");
        }

        [Test]
        public void AfterARollback_TimeCatchingUpRestoresNormalService()
        {
            Claim(out _);
            _clock.UtcNow = _clock.UtcNow.AddDays(-1);
            Claim(out _); // refused

            _clock.UtcNow = _clock.UtcNow.AddDays(2); // now one day AFTER the original claim

            var refusal = Claim(out var claimed);

            Assert.That(refusal, Is.EqualTo(ClaimRefusal.None));
            Assert.That(claimed.StreakDay, Is.EqualTo(2), "The streak continues once time is consistent again.");
        }

        [Test]
        public void RestoredFromSave_TheStreakContinuesAcrossSessions()
        {
            // The round trip that matters: claim, "close the app", reopen tomorrow, streak intact.
            Claim(out _);
            NextDay();
            Claim(out _);

            var lastClaim = _rewards.LastClaimUtc;
            var streak = _rewards.StreakDays;

            // Relaunch: new service constructed from persisted state.
            using var bus2 = new EventBus();
            var wallet2 = new Wallet(bus2);
            var restored = new DailyRewardService(wallet2, _clock, bus2, lastClaim, streak);

            _clock.UtcNow = _clock.UtcNow.AddDays(1);

            var refusal = restored.TryClaim(out var claimed);

            Assert.That(refusal, Is.EqualTo(ClaimRefusal.None));
            Assert.That(claimed.StreakDay, Is.EqualTo(3), "Day 3, continuing the persisted streak.");
        }

        [Test]
        public void NextReward_PreviewsWithoutClaiming()
        {
            // The UI shows "tomorrow: 150 coins" — previewing must never pay out or mutate anything.
            var preview = _rewards.NextReward;

            Assert.That(preview.coins, Is.GreaterThan(0));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.Zero);
            Assert.That(_rewards.StreakDays, Is.Zero);
        }

        [Test]
        public void ClaimPublishesForAnalyticsAndUi()
        {
            DailyRewardClaimed? seen = null;
            using var subscription = _bus.Subscribe<DailyRewardClaimed>(e => seen = e);

            Claim(out _);

            Assert.That(seen.HasValue, Is.True);
            Assert.That(seen.Value.StreakDay, Is.EqualTo(1));
        }
    }
}
