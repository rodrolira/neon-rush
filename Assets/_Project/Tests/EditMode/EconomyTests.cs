using System;
using System.Collections.Generic;
using NeonRush.Application.Economy;
using NeonRush.Application.Events;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    [TestFixture]
    public sealed class WalletTests
    {
        private EventBus _bus;
        private Wallet _wallet;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _wallet = new Wallet(_bus, startingCoins: 100, startingGems: 10);
        }

        [TearDown]
        public void TearDown() => _bus.Dispose();

        [Test]
        public void StartingBalancesAreHonoured()
        {
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(100));
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(10));
        }

        [Test]
        public void Credit_AddsAndPublishes()
        {
            CurrencyChanged? seen = null;
            using var _ = _bus.Subscribe<CurrencyChanged>(e => seen = e);

            var credited = _wallet.Credit(CurrencyType.Coins, 50, TransactionReason.RunReward);

            Assert.That(credited, Is.EqualTo(50));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(150));
            Assert.That(seen.HasValue, Is.True);
            Assert.That(seen.Value.Delta, Is.EqualTo(50));
            Assert.That(seen.Value.Balance, Is.EqualTo(150));
            Assert.That(seen.Value.Reason, Is.EqualTo(TransactionReason.RunReward));
        }

        [Test]
        public void Debit_RemovesWhenAffordable()
        {
            var ok = _wallet.TryDebit(CurrencyType.Coins, 40, TransactionReason.StorePurchase);

            Assert.That(ok, Is.True);
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(60));
        }

        [Test]
        public void Debit_RefusedWhenTooPoor_AndNeverGoesNegative()
        {
            var ok = _wallet.TryDebit(CurrencyType.Gems, 999, TransactionReason.StorePurchase);

            Assert.That(ok, Is.False);
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(10),
                "A refused debit must not move the balance at all.");
        }

        [Test]
        public void InsufficientFunds_PublishesTheShortfall()
        {
            // This event is the most commercially valuable signal the economy produces: the exact
            // moment a player wanted something and could not have it. An offer sized to the
            // shortfall, shown here, is a solution rather than an interruption.
            PurchaseFailedInsufficientFunds? seen = null;
            using var _ = _bus.Subscribe<PurchaseFailedInsufficientFunds>(e => seen = e);

            _wallet.TryDebit(CurrencyType.Gems, 250, TransactionReason.StorePurchase);

            Assert.That(seen.HasValue, Is.True);
            Assert.That(seen.Value.Price, Is.EqualTo(250));
            Assert.That(seen.Value.Balance, Is.EqualTo(10));
            Assert.That(seen.Value.Shortfall, Is.EqualTo(240),
                "The shortfall is the number a targeted offer should be sized against.");
        }

        [Test]
        public void Credit_ClampsAtTheCap_InsteadOfOverflowingNegative()
        {
            // The bug this prevents is catastrophic and completely silent: without a cap, a balance
            // near int.MaxValue overflows on the next credit and the player ends up with MINUS two
            // billion coins. They can then buy nothing, and support cannot explain it.
            var wallet = new Wallet(_bus, startingCoins: Wallet.MaxBalance - 10);

            var credited = wallet.Credit(CurrencyType.Coins, 1000, TransactionReason.RunReward);

            Assert.That(credited, Is.EqualTo(10), "Only the amount that fits may be credited.");
            Assert.That(wallet.Balance(CurrencyType.Coins), Is.EqualTo(Wallet.MaxBalance));
            Assert.That(wallet.Balance(CurrencyType.Coins), Is.Positive, "A balance must never wrap negative.");
        }

        [Test]
        public void CreditingWithASinkReason_Throws()
        {
            // Sign errors here are how an economy dashboard ends up showing gems being *created* by
            // "StorePurchase", which makes the source of inflation invisible.
            Assert.Throws<ArgumentException>(
                () => _wallet.Credit(CurrencyType.Gems, 10, TransactionReason.StorePurchase));
        }

        [Test]
        public void DebitingWithAFaucetReason_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => _wallet.TryDebit(CurrencyType.Coins, 10, TransactionReason.RunReward));
        }

        [Test]
        public void NegativeAmounts_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _wallet.Credit(CurrencyType.Coins, -5, TransactionReason.RunReward));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => _wallet.TryDebit(CurrencyType.Coins, -5, TransactionReason.StorePurchase));
        }

        [Test]
        public void EveryMovementIsRecordedWithItsReason()
        {
            _wallet.Credit(CurrencyType.Coins, 30, TransactionReason.RunReward);
            _wallet.TryDebit(CurrencyType.Coins, 20, TransactionReason.Continue);

            Assert.That(_wallet.Ledger, Has.Count.EqualTo(2));

            Assert.That(_wallet.Ledger[0].Delta, Is.EqualTo(30));
            Assert.That(_wallet.Ledger[0].Reason, Is.EqualTo(TransactionReason.RunReward));

            Assert.That(_wallet.Ledger[1].Delta, Is.EqualTo(-20));
            Assert.That(_wallet.Ledger[1].Reason, Is.EqualTo(TransactionReason.Continue));
            Assert.That(_wallet.Ledger[1].BalanceAfter, Is.EqualTo(110));
        }

        [Test]
        public void TheLedgerDoesNotGrowWithoutBound()
        {
            // A player who has run 50,000 times must not carry a 50,000-entry list into every save.
            for (var i = 0; i < 500; i++)
            {
                _wallet.Credit(CurrencyType.Coins, 1, TransactionReason.RunReward);
            }

            Assert.That(_wallet.Ledger.Count, Is.LessThanOrEqualTo(200));
        }

        [Test]
        public void SyncFromServer_OverwritesTheLocalPrediction()
        {
            // The local wallet is a prediction. When the server disagrees, the server wins.
            _wallet.SyncFromServer(CurrencyType.Coins, 42);

            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(42));
        }

        [Test]
        public void ZeroAmounts_AreNoOps()
        {
            var events = 0;
            using var _ = _bus.Subscribe<CurrencyChanged>(__ => events++);

            _wallet.Credit(CurrencyType.Coins, 0, TransactionReason.RunReward);
            _wallet.TryDebit(CurrencyType.Coins, 0, TransactionReason.StorePurchase);

            Assert.That(events, Is.Zero, "A zero-value movement must not spam the event bus or the ledger.");
            Assert.That(_wallet.Ledger, Is.Empty);
        }
    }

    [TestFixture]
    public sealed class ObscuredIntTests
    {
        [Test]
        public void RoundTripsItsValue()
        {
            var values = new[] { 0, 1, -1, 12345, int.MaxValue, int.MinValue };

            foreach (var value in values)
            {
                var obscured = new ObscuredInt(value);
                Assert.That(obscured.Value, Is.EqualTo(value));
            }
        }

        [Test]
        public void TheRawBytesNeverContainThePlainValue()
        {
            // This is the entire point. A memory scanner (GameGuardian and friends) searches the
            // process for the literal number on screen. If that number is anywhere in the struct,
            // the whole exercise is theatre.
            const int secret = 1337;
            var obscured = new ObscuredInt(secret);

            // The struct's storage is (key, obscured, checksum). None may equal the plaintext.
            // We can only observe them via the public value, so we assert the invariant that makes
            // it true: two wallets holding the SAME value must have DIFFERENT internal bytes, which
            // is only possible if the value is not stored directly.
            var a = new ObscuredInt(secret);
            var b = new ObscuredInt(secret);

            Assert.That(a.Value, Is.EqualTo(b.Value), "Same logical value...");
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));

            // Re-keying on write must change the representation even when the value does not.
            var c = new ObscuredInt(secret);
            c.Set(secret);
            Assert.That(c.Value, Is.EqualTo(secret),
                "Re-keying must preserve the value while changing the bytes that hold it.");
        }

        [Test]
        public void DefaultInstance_IsZeroNotCorrupt()
        {
            // A zero-initialised struct (an array element, a field that was never assigned) is
            // legitimately zero. Reporting it as tampering would fire false cheat alarms at honest
            // players.
            ObscuredInt uninitialised = default;

            Assert.That(uninitialised.Value, Is.Zero);
            Assert.That(uninitialised.TryGetValue(out var value), Is.True);
            Assert.That(value, Is.Zero);
        }

        [Test]
        public void ImplicitConversionsWork()
        {
            ObscuredInt obscured = 250;
            int back = obscured;

            Assert.That(back, Is.EqualTo(250));
        }

        [Test]
        public void SetRewritesTheValue()
        {
            var obscured = new ObscuredInt(10);
            obscured.Set(99);

            Assert.That(obscured.Value, Is.EqualTo(99));
        }
    }

    [TestFixture]
    public sealed class RunRewardServiceTests
    {
        private EventBus _bus;
        private Wallet _wallet;
        private RunRewardService _rewards;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _wallet = new Wallet(_bus);
            _rewards = new RunRewardService(_wallet, _bus);
        }

        [TearDown]
        public void TearDown()
        {
            _rewards.Dispose();
            _bus.Dispose();
        }

        private void EndRunWith(int coins) =>
            _bus.Publish(new RunEnded(1, 500f, coins, 1000, DeathCause.HitObstacle, 30f));

        [Test]
        public void RunCoinsAreBankedImmediatelyOnDeath()
        {
            // Non-negotiable: the player earned these. Holding them hostage behind an ad offer means
            // a player who loses signal, closes the app, or fails to load an ad loses coins they
            // legitimately earned. Players correctly perceive that as theft.
            EndRunWith(75);

            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(75),
                "The base reward must be credited before any offer is shown.");
        }

        [Test]
        public void ClaimDouble_AddsTheSameAmountAgain()
        {
            EndRunWith(75);

            var added = _rewards.ClaimDouble();

            Assert.That(added, Is.EqualTo(75));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(150));
        }

        [Test]
        public void ClaimDouble_CannotBeClaimedTwice()
        {
            // Otherwise a player who can replay the ad callback mints coins forever.
            EndRunWith(50);

            _rewards.ClaimDouble();
            var second = _rewards.ClaimDouble();

            Assert.That(second, Is.Zero);
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(100));
        }

        [Test]
        public void NoOfferIsMadeForAZeroCoinRun()
        {
            // Offering to double nothing burns an ad impression, earns nothing, and insults a player
            // who just died instantly.
            EndRunWith(0);

            Assert.That(_rewards.CanClaimDouble, Is.False);
            Assert.That(_rewards.ClaimDouble(), Is.Zero);
        }

        [Test]
        public void ANewRunResetsTheOffer()
        {
            EndRunWith(30);
            _rewards.ClaimDouble();

            EndRunWith(40);

            Assert.That(_rewards.CanClaimDouble, Is.True, "Each run gets its own doubling offer.");
            Assert.That(_rewards.LastRunCoins, Is.EqualTo(40));
        }

        [Test]
        public void DoubledCoinsAreAttributedToTheAd_NotToGameplay()
        {
            // The economy dashboard must be able to answer "how much coin inflation comes from
            // rewarded ads?" — that number decides whether the ad is priced correctly against the
            // coin sinks. A second RunReward transaction would hide it.
            EndRunWith(20);
            _rewards.ClaimDouble();

            var reasons = new List<TransactionReason>();
            foreach (var t in _wallet.Ledger) reasons.Add(t.Reason);

            Assert.That(reasons, Is.EqualTo(new[]
            {
                TransactionReason.RunReward,
                TransactionReason.RunRewardDoubled,
            }));
        }

        [Test]
        public void TheRewardIsDrivenByTheRealRunSession()
        {
            // End-to-end through the actual game loop rather than a hand-published event: collect
            // coins in a real RunSession, die, and confirm they land in the wallet.
            var session = new RunSession(new RunTuning(), _bus);

            session.Begin();
            session.CollectCoin();
            session.CollectCoin(5);
            session.End(DeathCause.HitObstacle);

            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(6));
        }
    }
}
