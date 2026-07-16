using System;
using NeonRush.Application.Events;
using NeonRush.Application.Progression;
using NeonRush.Application.Save;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Save;
using NeonRush.Infrastructure.Save;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>A controllable clock. Time is an input, so in tests it is a dial we turn.</summary>
    internal sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public TimeSpan ElapsedRealtime { get; set; } = TimeSpan.Zero;
        public bool IsServerTimeAuthoritative { get; set; } = true;
        public TimeSpan DeviceClockDrift { get; set; } = TimeSpan.Zero;
    }

    [TestFixture]
    public sealed class SaveServiceTests
    {
        private EventBus _bus;
        private InMemorySaveStore _store;
        private Wallet _wallet;
        private PlayerProfile _profile;
        private Inventory _inventory;
        private FakeClock _clock;
        private SaveService _save;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _store = new InMemorySaveStore();
            _wallet = new Wallet(_bus);
            _profile = new PlayerProfile(_bus);
            _inventory = new Inventory(_bus);
            _clock = new FakeClock();
            _save = new SaveService(_store, _wallet, _profile, _inventory, _bus, _clock);
        }

        [TearDown]
        public void TearDown()
        {
            _profile.Dispose();
            _bus.Dispose();
        }

        private void EndRun(int coins = 10, int score = 500, float distance = 300f) =>
            _bus.Publish(new RunEnded(1, distance, coins, score, DeathCause.HitObstacle, 30f));

        [Test]
        public void CurrencyChange_MarksDirtyButDoesNotWriteImmediately()
        {
            // Writing on every coin pickup means hundreds of disk writes per run: battery, flash
            // wear, and an I/O stall that shows up as a frame hitch mid-gameplay.
            _wallet.Credit(CurrencyType.Coins, 5, TransactionReason.RunReward);

            Assert.That(_save.HasUnsavedChanges, Is.True);
            Assert.That(_store.WriteCount, Is.Zero, "A single coin must not trigger a disk write.");
        }

        [Test]
        public void ManyChanges_CollapseIntoOneWrite()
        {
            for (var i = 0; i < 100; i++)
            {
                _wallet.Credit(CurrencyType.Coins, 1, TransactionReason.RunReward);
            }

            _save.Tick(6f); // past the 5s debounce

            Assert.That(_store.WriteCount, Is.EqualTo(1),
                "A run's worth of pickups must collapse into a single write.");
        }

        [Test]
        public void DebounceDoesNotWriteBeforeItsTime()
        {
            _wallet.Credit(CurrencyType.Coins, 1, TransactionReason.RunReward);

            _save.Tick(1f);
            _save.Tick(1f);

            Assert.That(_store.WriteCount, Is.Zero);

            _save.Tick(4f); // now past 5s total

            Assert.That(_store.WriteCount, Is.EqualTo(1));
        }

        [Test]
        public void TickWithNothingDirty_NeverWrites()
        {
            _save.Tick(100f);

            Assert.That(_store.WriteCount, Is.Zero, "An idle game must not write to disk at all.");
        }

        [Test]
        public void Flush_WritesImmediately()
        {
            // This is the OnApplicationPause path — the last moment the OS guarantees us.
            _wallet.Credit(CurrencyType.Coins, 7, TransactionReason.RunReward);

            Assert.That(_save.Flush(), Is.True);
            Assert.That(_store.WriteCount, Is.EqualTo(1));
            Assert.That(_save.HasUnsavedChanges, Is.False);
        }

        [Test]
        public void AFailedWrite_StaysDirtyAndRetries()
        {
            // Disk full, permissions revoked. Clearing the dirty flag here would silently drop the
            // player's progress on the floor and never try again.
            _wallet.Credit(CurrencyType.Coins, 7, TransactionReason.RunReward);

            _store.FailWrites = true;
            Assert.That(_save.Flush(), Is.False);
            Assert.That(_save.HasUnsavedChanges, Is.True, "A failed write must remain pending.");

            _store.FailWrites = false;
            Assert.That(_save.Flush(), Is.True);
            Assert.That(_store.WriteCount, Is.EqualTo(1));
        }

        [Test]
        public void Capture_TakesTheWholeGameState()
        {
            _wallet.Credit(CurrencyType.Coins, 120, TransactionReason.RunReward);
            _wallet.Credit(CurrencyType.Gems, 3, TransactionReason.DailyReward);
            EndRun(coins: 10, score: 900, distance: 450f);

            var data = _save.Capture();

            // 120, not 130: this fixture has no RunRewardService, so the run's 10 coins are never
            // banked. Capture reflects the wallet as it actually is, not as gameplay implies.
            Assert.That(data.Coins, Is.EqualTo(120));
            Assert.That(data.Gems, Is.EqualTo(3));
            Assert.That(data.BestScore, Is.EqualTo(900));
            Assert.That(data.TotalRuns, Is.EqualTo(1));
            Assert.That(data.TotalDistance, Is.EqualTo(450));
        }

        [Test]
        public void SavedTimestampComesFromTheTrustedClock_NotTheDevice()
        {
            // The save timestamp decides cloud-merge conflicts. If it came from the device clock, a
            // player could make their local (cheated) save always win the merge by setting their
            // phone forward.
            _clock.UtcNow = new DateTime(2030, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            _wallet.Credit(CurrencyType.Coins, 1, TransactionReason.RunReward);
            var data = _save.Capture();

            Assert.That(data.SavedAtUtc, Is.EqualTo(_clock.UtcNow));
        }

        [Test]
        public void Dispose_FlushesUnsavedChanges()
        {
            _wallet.Credit(CurrencyType.Coins, 42, TransactionReason.RunReward);

            _save.Dispose();

            Assert.That(_store.WriteCount, Is.EqualTo(1),
                "Disposing without flushing would discard everything since the last debounce.");
        }

        [Test]
        public void ARoundTripRestoresEverything()
        {
            _wallet.Credit(CurrencyType.Coins, 250, TransactionReason.RunReward);
            _wallet.Credit(CurrencyType.Gems, 15, TransactionReason.IapPurchase);
            EndRun(score: 1234, distance: 800f);
            _save.Flush();

            // Simulate a relaunch: fresh bus, fresh objects, same store.
            using var bus2 = new EventBus();
            var loaded = _store.Load();

            Assert.That(loaded.Restored, Is.True);

            var wallet2 = new Wallet(bus2, loaded.Data.Coins, loaded.Data.Gems);
            using var profile2 = new PlayerProfile(bus2, loaded.Data);

            Assert.That(wallet2.Balance(CurrencyType.Coins), Is.EqualTo(250));
            Assert.That(wallet2.Balance(CurrencyType.Gems), Is.EqualTo(15));
            Assert.That(profile2.BestScore, Is.EqualTo(1234));
            Assert.That(profile2.TotalRuns, Is.EqualTo(1));
            Assert.That(profile2.TotalDistance, Is.EqualTo(800));
        }

        [Test]
        public void AFreshInstall_LoadsAUsableProfile_AndIsNotAnError()
        {
            var empty = new InMemorySaveStore();
            var result = empty.Load();

            Assert.That(result.Failure, Is.EqualTo(LoadFailure.NotFound));
            Assert.That(result.Restored, Is.False);
            Assert.That(result.Data, Is.Not.Null, "A new install must still get a usable profile.");
            Assert.That(result.Data.Coins, Is.Zero);
        }

        [Test]
        public void ACorruptSave_StillBootsTheGame()
        {
            // A corrupt save is a bad day for one player. A game that refuses to start is a one-star
            // review and an uninstall.
            _store.NextLoadFailure = LoadFailure.Corrupt;

            var result = _store.Load();

            Assert.That(result.Restored, Is.False);
            Assert.That(result.Failure, Is.EqualTo(LoadFailure.Corrupt));
            Assert.That(result.Data, Is.Not.Null, "The game must always be able to boot.");
        }

        [Test]
        public void LoadReturnsACopy_NotTheLiveStoredInstance()
        {
            // Otherwise a caller can mutate the "stored" data without ever writing it, which hides
            // save bugs instead of exposing them.
            _wallet.Credit(CurrencyType.Coins, 10, TransactionReason.RunReward);
            _save.Flush();

            var first = _store.Load();
            first.Data.Coins = 999999;

            var second = _store.Load();

            Assert.That(second.Data.Coins, Is.EqualTo(10));
        }
    }

    [TestFixture]
    public sealed class PlayerProfileTests
    {
        private EventBus _bus;
        private PlayerProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _profile = new PlayerProfile(_bus);
        }

        [TearDown]
        public void TearDown()
        {
            _profile.Dispose();
            _bus.Dispose();
        }

        private void EndRun(int score, float distance = 100f) =>
            _bus.Publish(new RunEnded(1, distance, 0, score, DeathCause.HitObstacle, 10f));

        [Test]
        public void BestScoreOnlyGoesUp()
        {
            EndRun(500);
            Assert.That(_profile.BestScore, Is.EqualTo(500));
            Assert.That(_profile.LastRunWasPersonalBest, Is.True);

            EndRun(200);
            Assert.That(_profile.BestScore, Is.EqualTo(500), "A worse run must not lower the best score.");
            Assert.That(_profile.LastRunWasPersonalBest, Is.False);

            EndRun(900);
            Assert.That(_profile.BestScore, Is.EqualTo(900));
            Assert.That(_profile.LastRunWasPersonalBest, Is.True);
        }

        [Test]
        public void LifetimeTotalsAccumulate()
        {
            EndRun(100, distance: 250f);
            EndRun(100, distance: 400f);

            Assert.That(_profile.TotalRuns, Is.EqualTo(2));
            Assert.That(_profile.TotalDistance, Is.EqualTo(650));
        }

        [Test]
        public void LoadedProfileIsRestored()
        {
            var data = SaveData.NewPlayer();
            data.BestScore = 4321;
            data.TotalRuns = 99;
            data.TotalDistance = 123456;

            using var bus = new EventBus();
            using var profile = new PlayerProfile(bus, data);

            Assert.That(profile.BestScore, Is.EqualTo(4321));
            Assert.That(profile.TotalRuns, Is.EqualTo(99));
            Assert.That(profile.TotalDistance, Is.EqualTo(123456));
        }
    }
}
