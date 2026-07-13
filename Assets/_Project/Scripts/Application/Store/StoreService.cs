using System;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;

namespace NeonRush.Application.Store
{
    /// <summary>Why a purchase did not happen.</summary>
    public enum PurchaseFailure
    {
        None = 0,
        UnknownItem = 1,
        AlreadyOwned = 2,
        InsufficientFunds = 3,
        StoreUnavailable = 4,
        Cancelled = 5,

        /// <summary>
        /// The platform said the player paid, but the server refused the receipt.
        /// This is either an attack or a serious backend fault. Never grant on this.
        /// </summary>
        ReceiptRejected = 6,

        PaymentFailed = 7,
    }

    /// <summary>Outcome of a purchase attempt.</summary>
    public readonly struct PurchaseOutcome
    {
        public readonly bool Succeeded;
        public readonly PurchaseFailure Failure;
        public readonly string ItemId;

        private PurchaseOutcome(bool succeeded, PurchaseFailure failure, string itemId)
        {
            Succeeded = succeeded;
            Failure = failure;
            ItemId = itemId;
        }

        public static PurchaseOutcome Success(string itemId) => new(true, PurchaseFailure.None, itemId);

        public static PurchaseOutcome Failed(PurchaseFailure failure, string itemId = null) =>
            new(false, failure, itemId);
    }

    /// <summary>A purchase completed. Analytics, UI and the save layer all listen.</summary>
    public readonly struct PurchaseCompleted
    {
        public readonly string ItemId;
        public readonly bool WasRealMoney;

        public PurchaseCompleted(string itemId, bool wasRealMoney)
        {
            ItemId = itemId;
            WasRealMoney = wasRealMoney;
        }
    }

    /// <summary>
    /// The only place in the game where anything is bought.
    ///
    /// Two payment rails, and they are not symmetrical — which is the whole reason this class exists
    /// rather than the UI simply calling the wallet:
    ///
    /// <b>Soft/premium currency</b> (coins, gems): the wallet is local, so this is synchronous and
    /// can be decided on the spot. Debit, grant, done.
    ///
    /// <b>Real money</b>: the client is not allowed to decide anything. The store hands back a
    /// receipt, the receipt goes to the server, the SERVER decides whether it is genuine and unused,
    /// and only then is anything granted. The one-line summary of every drained mobile economy is:
    /// <i>they granted the item when the SDK said "purchased".</i> The SDK runs on the attacker's
    /// phone. Its word is worth nothing.
    ///
    /// Pure C#. The store and the validator are ports, so the entire purchase pipeline —
    /// including the receipt-rejected path, which is the one that matters most and the one nobody
    /// ever tests — runs in a unit test.
    /// </summary>
    public sealed class StoreService
    {
        private readonly StoreCatalog _catalog;
        private readonly Wallet _wallet;
        private readonly Inventory _inventory;
        private readonly IIapService _iap;
        private readonly IReceiptValidator _validator;
        private readonly IEventBus _bus;
        private readonly IAdsService _ads;

        /// <summary>Guards against a double-tap starting two purchases of the same item.</summary>
        private bool _purchaseInFlight;

        public StoreService(
            StoreCatalog catalog,
            Wallet wallet,
            Inventory inventory,
            IIapService iap,
            IReceiptValidator validator,
            IAdsService ads,
            IEventBus bus)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _iap = iap ?? throw new ArgumentNullException(nameof(iap));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _ads = ads ?? throw new ArgumentNullException(nameof(ads));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        /// <summary>True while a purchase is being processed. The UI must disable its buy buttons.</summary>
        public bool IsPurchaseInFlight => _purchaseInFlight;

        /// <summary>
        /// Buys an item with in-game currency. Synchronous — the wallet is local.
        /// </summary>
        public PurchaseOutcome PurchaseWithCurrency(string itemId)
        {
            if (!_catalog.TryGet(itemId, out var item))
            {
                return PurchaseOutcome.Failed(PurchaseFailure.UnknownItem, itemId);
            }

            if (item.Price.IsRealMoney)
            {
                throw new InvalidOperationException(
                    $"'{itemId}' is a real-money product. Use {nameof(PurchaseWithMoney)}.");
            }

            // Checked BEFORE the debit. Selling a player a skin they already own is not a rounding
            // error, it is taking their money for nothing — and it is always a mis-tap, never intent.
            if (!item.IsConsumable && _inventory.Has(itemId))
            {
                return PurchaseOutcome.Failed(PurchaseFailure.AlreadyOwned, itemId);
            }

            // TryDebit publishes PurchaseFailedInsufficientFunds on failure, which is the signal an
            // offer should be targeted against. See Wallet.
            if (!_wallet.TryDebit(item.Price.Currency, item.Price.Amount, TransactionReason.StorePurchase))
            {
                return PurchaseOutcome.Failed(PurchaseFailure.InsufficientFunds, itemId);
            }

            GrantEntitlements(item);

            _bus.Publish(new PurchaseCompleted(itemId, wasRealMoney: false));

            return PurchaseOutcome.Success(itemId);
        }

        /// <summary>
        /// Buys an item with real money.
        ///
        /// The flow, and every step of it is load-bearing:
        ///   store.Purchase → receipt → server validates → THEN grant.
        ///
        /// Nothing is granted between step 1 and step 3. A player who completes a payment and then
        /// kills the app before validation is not lost: the platform store will re-deliver the
        /// pending transaction on the next launch, which is why the SDK adapter must not "consume" a
        /// purchase until the server has confirmed it.
        /// </summary>
        public void PurchaseWithMoney(string itemId, Action<PurchaseOutcome> onFinished)
        {
            if (_purchaseInFlight)
            {
                onFinished?.Invoke(PurchaseOutcome.Failed(PurchaseFailure.StoreUnavailable, itemId));
                return;
            }

            if (!_catalog.TryGet(itemId, out var item))
            {
                onFinished?.Invoke(PurchaseOutcome.Failed(PurchaseFailure.UnknownItem, itemId));
                return;
            }

            if (!item.Price.IsRealMoney)
            {
                throw new InvalidOperationException(
                    $"'{itemId}' is a currency product. Use {nameof(PurchaseWithCurrency)}.");
            }

            if (!item.IsConsumable && _inventory.Has(itemId))
            {
                onFinished?.Invoke(PurchaseOutcome.Failed(PurchaseFailure.AlreadyOwned, itemId));
                return;
            }

            if (!_iap.IsInitialised)
            {
                onFinished?.Invoke(PurchaseOutcome.Failed(PurchaseFailure.StoreUnavailable, itemId));
                return;
            }

            _purchaseInFlight = true;

            _iap.Purchase(item.Price.ProductId, result =>
            {
                if (!result.IsPurchased)
                {
                    _purchaseInFlight = false;

                    var failure = result.Status switch
                    {
                        IapStatus.Cancelled => PurchaseFailure.Cancelled,
                        IapStatus.AlreadyOwned => PurchaseFailure.AlreadyOwned,
                        IapStatus.Unavailable => PurchaseFailure.StoreUnavailable,
                        _ => PurchaseFailure.PaymentFailed,
                    };

                    onFinished?.Invoke(PurchaseOutcome.Failed(failure, itemId));
                    return;
                }

                // The store says they paid. That is a CLAIM, made on hardware the attacker controls.
                // It buys nothing until the server agrees.
                _validator.Validate(result.Purchase, validation =>
                {
                    _purchaseInFlight = false;

                    if (!validation.IsValid)
                    {
                        // Either an attack (a replayed or forged receipt) or a serious backend fault.
                        // Granting here "just in case" is how an economy is drained.
                        onFinished?.Invoke(PurchaseOutcome.Failed(PurchaseFailure.ReceiptRejected, itemId));
                        return;
                    }

                    GrantEntitlements(item);

                    _bus.Publish(new PurchaseCompleted(itemId, wasRealMoney: true));

                    onFinished?.Invoke(PurchaseOutcome.Success(itemId));
                });
            });
        }

        /// <summary>
        /// Restores non-consumable purchases. Required by Apple; needed by anyone who reinstalls.
        /// </summary>
        public void RestorePurchases(Action<bool> onFinished) => _iap.RestorePurchases(onFinished);

        /// <summary>
        /// Hands the player everything the item entitles them to.
        ///
        /// Called from exactly two places — after a currency debit, and after a server-validated
        /// receipt — so there is no path to an entitlement that skipped payment.
        /// </summary>
        private void GrantEntitlements(StoreItem item)
        {
            if (item.GrantsCoins > 0)
            {
                _wallet.Credit(CurrencyType.Coins, item.GrantsCoins, TransactionReason.IapPurchase);
            }

            if (item.GrantsGems > 0)
            {
                _wallet.Credit(CurrencyType.Gems, item.GrantsGems, TransactionReason.IapPurchase);
            }

            // The item itself, unless it is pure currency (you do not "own" a gem pack).
            if (!item.IsConsumable)
            {
                _inventory.Grant(item.Id);
            }

            foreach (var granted in item.GrantsItems)
            {
                _inventory.Grant(granted);
            }

            if (item.Kind == ItemKind.AdRemoval)
            {
                // Interstitials only. Rewarded ads stay available on purpose — the player bought
                // freedom from interruption, not from opportunity. See IAdsService.
                _ads.DisableInterstitials();
            }
        }
    }
}
