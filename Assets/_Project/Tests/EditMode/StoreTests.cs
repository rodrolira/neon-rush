using System;
using System.Collections.Generic;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>A billing service whose every outcome is a dial.</summary>
    internal sealed class FakeIapService : IIapService
    {
        public bool IsInitialised { get; set; } = true;
        public IapStatus NextStatus { get; set; } = IapStatus.Purchased;
        public readonly List<string> Purchased = new();

        public string GetLocalisedPrice(string productId) => "0,99 €";

        public void Purchase(string productId, Action<IapResult> onFinished)
        {
            if (NextStatus != IapStatus.Purchased)
            {
                onFinished(new IapResult(NextStatus));
                return;
            }

            Purchased.Add(productId);

            onFinished(new IapResult(
                IapStatus.Purchased,
                new IapPurchase(productId, "receipt", Guid.NewGuid().ToString("N"))));
        }

        public void RestorePurchases(Action<bool> onFinished) => onFinished(true);
    }

    /// <summary>A receipt validator that can be told to reject. This is the dial that matters.</summary>
    internal sealed class FakeReceiptValidator : IReceiptValidator
    {
        public bool Accept { get; set; } = true;
        public int Calls { get; private set; }

        public void Validate(IapPurchase purchase, Action<ValidationResult> onFinished)
        {
            Calls++;
            onFinished(Accept ? ValidationResult.Valid() : ValidationResult.Invalid("test rejection"));
        }
    }

    internal sealed class SpyAdsService : IAdsService
    {
        public bool IsRewardedReady => true;
        public bool IsInterstitialReady => !InterstitialsDisabled;
        public bool InterstitialsDisabled { get; private set; }

        public void Preload() { }
        public void ShowRewarded(NeonRush.Domain.Ads.AdPlacement p, Action<NeonRush.Domain.Ads.AdResult> cb) => cb(NeonRush.Domain.Ads.AdResult.Completed);
        public void ShowInterstitial(NeonRush.Domain.Ads.AdPlacement p, Action<NeonRush.Domain.Ads.AdResult> cb) => cb(NeonRush.Domain.Ads.AdResult.Completed);
        public void DisableInterstitials() => InterstitialsDisabled = true;
    }

    [TestFixture]
    public sealed class StoreServiceTests
    {
        private EventBus _bus;
        private Wallet _wallet;
        private Inventory _inventory;
        private FakeIapService _iap;
        private FakeReceiptValidator _validator;
        private SpyAdsService _ads;
        private StoreCatalog _catalog;
        private StoreService _store;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _wallet = new Wallet(_bus, startingCoins: 10_000, startingGems: 1_000);
            _inventory = new Inventory(_bus);
            _iap = new FakeIapService();
            _validator = new FakeReceiptValidator();
            _ads = new SpyAdsService();
            _catalog = StoreCatalog.Default();
            _store = new StoreService(_catalog, _wallet, _inventory, _iap, _validator, _ads, _bus);
        }

        [TearDown]
        public void TearDown() => _bus.Dispose();

        // -------------------------------------------------------------------------------
        // Currency purchases
        // -------------------------------------------------------------------------------

        [Test]
        public void BuyingWithCoins_DebitsAndGrants()
        {
            var outcome = _store.PurchaseWithCurrency("char_nova"); // 2,500 coins

            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(7_500));
            Assert.That(_inventory.Has("char_nova"), Is.True);
        }

        [Test]
        public void BuyingSomethingYouAlreadyOwn_IsRefusedBeforeTheDebit()
        {
            // Selling a player a skin they already own is not a rounding error, it is taking their
            // money for nothing — and it is always a mis-tap, never intent.
            _store.PurchaseWithCurrency("char_nova");
            var balance = _wallet.Balance(CurrencyType.Coins);

            var outcome = _store.PurchaseWithCurrency("char_nova");

            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.Failure, Is.EqualTo(PurchaseFailure.AlreadyOwned));
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(balance),
                "The player must not be charged a second time.");
        }

        [Test]
        public void BuyingWithoutEnoughCurrency_ChangesNothing()
        {
            var poor = new Wallet(_bus, startingCoins: 10);
            var store = new StoreService(_catalog, poor, _inventory, _iap, _validator, _ads, _bus);

            var outcome = store.PurchaseWithCurrency("char_rook"); // 7,500 coins

            Assert.That(outcome.Failure, Is.EqualTo(PurchaseFailure.InsufficientFunds));
            Assert.That(poor.Balance(CurrencyType.Coins), Is.EqualTo(10));
            Assert.That(_inventory.Has("char_rook"), Is.False);
        }

        [Test]
        public void InsufficientFunds_StillPublishesTheShortfall()
        {
            // The qualified-lead signal. See Wallet.
            var poor = new Wallet(_bus, startingGems: 100);
            var store = new StoreService(_catalog, poor, _inventory, _iap, _validator, _ads, _bus);

            PurchaseFailedInsufficientFunds? seen = null;
            using var _ = _bus.Subscribe<PurchaseFailedInsufficientFunds>(e => seen = e);

            store.PurchaseWithCurrency("skin_nova_void"); // 350 gems

            Assert.That(seen.HasValue, Is.True);
            Assert.That(seen.Value.Shortfall, Is.EqualTo(250));
        }

        [Test]
        public void BuyingAnUnknownItem_Fails()
        {
            Assert.That(_store.PurchaseWithCurrency("does_not_exist").Failure,
                Is.EqualTo(PurchaseFailure.UnknownItem));
        }

        [Test]
        public void UsingTheWrongPaymentRail_Throws()
        {
            // A programmer error, and one that would otherwise fail in a confusing way at runtime.
            Assert.Throws<InvalidOperationException>(() => _store.PurchaseWithCurrency("gems_500"));
            Assert.Throws<InvalidOperationException>(() => _store.PurchaseWithMoney("char_nova", _ => { }));
        }

        // -------------------------------------------------------------------------------
        // Real money — the paths that matter
        // -------------------------------------------------------------------------------

        [Test]
        public void AValidatedPurchase_GrantsEverything()
        {
            PurchaseOutcome outcome = default;
            _store.PurchaseWithMoney("bundle_starter", o => outcome = o);

            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(1_500));   // 1,000 + 500
            Assert.That(_wallet.Balance(CurrencyType.Coins), Is.EqualTo(15_000)); // 10,000 + 5,000
            Assert.That(_inventory.Has("char_nova"), Is.True);
            Assert.That(_inventory.Has("skin_nova_void"), Is.True);
        }

        [Test]
        public void AREJECTED_RECEIPT_GRANTS_NOTHING()
        {
            // THE test. The one-line summary of every drained mobile economy is: "they granted the
            // item when the SDK said purchased". The SDK runs on the attacker's phone; its word is
            // worth nothing. Only the server's verdict may unlock anything.
            _validator.Accept = false;

            PurchaseOutcome outcome = default;
            _store.PurchaseWithMoney("gems_5000", o => outcome = o);

            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.Failure, Is.EqualTo(PurchaseFailure.ReceiptRejected));
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(1_000),
                "A rejected receipt must not create a single gem.");
        }

        [Test]
        public void EveryRealMoneyPurchase_IsValidatedServerSide()
        {
            _store.PurchaseWithMoney("gems_100", _ => { });

            Assert.That(_validator.Calls, Is.EqualTo(1),
                "No real-money grant may ever bypass the receipt validator.");
        }

        [Test]
        public void ACancelledPurchase_IsNotAnError_AndGrantsNothing()
        {
            _iap.NextStatus = IapStatus.Cancelled;

            PurchaseOutcome outcome = default;
            _store.PurchaseWithMoney("gems_500", o => outcome = o);

            Assert.That(outcome.Failure, Is.EqualTo(PurchaseFailure.Cancelled));
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(1_000));
            Assert.That(_validator.Calls, Is.Zero, "A cancelled purchase produces no receipt to validate.");
        }

        [Test]
        public void AFailedPayment_GrantsNothing()
        {
            _iap.NextStatus = IapStatus.Failed;

            PurchaseOutcome outcome = default;
            _store.PurchaseWithMoney("gems_500", o => outcome = o);

            Assert.That(outcome.Failure, Is.EqualTo(PurchaseFailure.PaymentFailed));
            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(1_000));
        }

        [Test]
        public void AnUninitialisedStore_FailsFastInsteadOfHanging()
        {
            _iap.IsInitialised = false;

            PurchaseOutcome outcome = default;
            _store.PurchaseWithMoney("gems_500", o => outcome = o);

            Assert.That(outcome.Failure, Is.EqualTo(PurchaseFailure.StoreUnavailable));
        }

        [Test]
        public void TheCallbackAlwaysFires()
        {
            // If it does not, the store UI spins forever behind a modal and the player force-quits —
            // possibly mid-payment.
            foreach (var status in new[] { IapStatus.Purchased, IapStatus.Cancelled, IapStatus.Failed, IapStatus.Unavailable })
            {
                _iap.NextStatus = status;

                var fired = false;
                _store.PurchaseWithMoney("gems_100", _ => fired = true);

                Assert.That(fired, Is.True, $"Callback must fire for {status}.");
            }
        }

        [Test]
        public void BuyingAdRemoval_DisablesInterstitialsButNotRewardedAds()
        {
            // The player bought freedom from interruption, not from opportunity. Removing their
            // ability to double their coins would be taking away a feature they still want.
            _store.PurchaseWithMoney("no_ads", _ => { });

            Assert.That(_ads.InterstitialsDisabled, Is.True);
            Assert.That(_ads.IsRewardedReady, Is.True, "Rewarded ads must survive an ad-removal purchase.");
        }

        [Test]
        public void CurrencyPacksAreConsumable_AndCanBeBoughtRepeatedly()
        {
            _store.PurchaseWithMoney("gems_100", _ => { });
            _store.PurchaseWithMoney("gems_100", _ => { });

            Assert.That(_wallet.Balance(CurrencyType.Gems), Is.EqualTo(1_200));
        }

        [Test]
        public void PurchaseCompleted_IsPublishedWithItsRail()
        {
            var events = new List<PurchaseCompleted>();
            using var _ = _bus.Subscribe<PurchaseCompleted>(e => events.Add(e));

            _store.PurchaseWithCurrency("char_nova");
            _store.PurchaseWithMoney("gems_100", __ => { });

            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events[0].WasRealMoney, Is.False);
            Assert.That(events[1].WasRealMoney, Is.True);
        }
    }

    [TestFixture]
    public sealed class InventoryTests
    {
        private EventBus _bus;
        private Inventory _inventory;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _inventory = new Inventory(_bus);
        }

        [TearDown]
        public void TearDown() => _bus.Dispose();

        [Test]
        public void GrantIsIdempotent()
        {
            // Grants arrive from a purchase, a cloud sync, a season reward and a support grant, and
            // they WILL overlap. Throwing on a duplicate would turn a routine re-sync into a crash.
            Assert.That(_inventory.Grant("skin_a"), Is.True);
            Assert.That(_inventory.Grant("skin_a"), Is.False);
            Assert.That(_inventory.Owned, Has.Count.EqualTo(1));
        }

        [Test]
        public void GrantPublishesOnlyOnce()
        {
            var count = 0;
            using var _ = _bus.Subscribe<ItemGranted>(__ => count++);

            _inventory.Grant("skin_a");
            _inventory.Grant("skin_a");

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void ServerMergeIsAUnion_AndNeverLosesAnOfflinePurchase()
        {
            // The player bought a skin offline a moment ago; it has not reached the server yet.
            // Replacing the local set with the server's would silently delete it — and losing a
            // purchase is unforgivable.
            _inventory.Grant("bought_offline");

            _inventory.MergeFromServer(new[] { "bought_on_old_phone" });

            Assert.That(_inventory.Has("bought_offline"), Is.True, "An offline purchase must survive a server merge.");
            Assert.That(_inventory.Has("bought_on_old_phone"), Is.True);
        }

        [Test]
        public void RestoredFromSave()
        {
            using var bus = new EventBus();
            var inventory = new Inventory(bus, new[] { "a", "b" });

            Assert.That(inventory.Has("a"), Is.True);
            Assert.That(inventory.Has("b"), Is.True);
            Assert.That(inventory.Has("c"), Is.False);
        }

        [Test]
        public void CatalogRejectsDuplicateIds()
        {
            // Two things claiming the same entitlement key means what the player owns depends on
            // load order.
            var catalog = new StoreCatalog();
            catalog.Add(new StoreItem("x", "X", ItemKind.Skin, Price.InCurrency(CurrencyType.Coins, 10)));

            Assert.Throws<ArgumentException>(
                () => catalog.Add(new StoreItem("x", "X again", ItemKind.Board, Price.InCurrency(CurrencyType.Gems, 5))));
        }
    }
}
