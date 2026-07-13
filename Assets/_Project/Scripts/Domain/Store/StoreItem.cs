using System;
using System.Collections.Generic;
using NeonRush.Domain.Economy;

namespace NeonRush.Domain.Store
{
    /// <summary>What a store item actually is.</summary>
    public enum ItemKind
    {
        /// <summary>A playable character. Cosmetic only — see the note in <see cref="StoreItem"/>.</summary>
        Character = 0,

        /// <summary>A skin for a character.</summary>
        Skin = 1,

        /// <summary>A hoverboard.</summary>
        Board = 2,

        /// <summary>A permanent powerup upgrade (Coin Magnet lvl 3, etc.).</summary>
        PowerupUpgrade = 3,

        /// <summary>A bundle of currency, bought with real money.</summary>
        CurrencyPack = 4,

        /// <summary>A bundle of several items at a discount.</summary>
        Bundle = 5,

        /// <summary>The season pass.</summary>
        SeasonPass = 6,

        /// <summary>Removes interstitial ads permanently.</summary>
        AdRemoval = 7,
    }

    /// <summary>What an item costs. Exactly one of the two payment rails.</summary>
    public readonly struct Price
    {
        /// <summary>The in-game currency, when this is a soft/premium-currency purchase.</summary>
        public readonly CurrencyType Currency;

        /// <summary>Amount of <see cref="Currency"/>. Zero when this is a real-money item.</summary>
        public readonly int Amount;

        /// <summary>
        /// The store product id, when this is a real-money purchase (e.g. "com.mooncatstudio.neonrush.gems_500").
        /// Null for currency purchases.
        /// </summary>
        public readonly string ProductId;

        private Price(CurrencyType currency, int amount, string productId)
        {
            Currency = currency;
            Amount = amount;
            ProductId = productId;
        }

        /// <summary>True when this item is bought with real money through the platform store.</summary>
        public bool IsRealMoney => ProductId != null;

        public static Price InCurrency(CurrencyType currency, int amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "A currency price must be positive.");

            return new Price(currency, amount, productId: null);
        }

        public static Price InRealMoney(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Product id required.", nameof(productId));

            return new Price(default, 0, productId);
        }
    }

    /// <summary>
    /// One thing the player can buy.
    ///
    /// <b>Everything purchasable in Neon Rush is cosmetic or convenience — never power.</b> That is a
    /// deliberate and load-bearing constraint, not squeamishness. In an endless runner with a
    /// leaderboard, selling power destroys the only thing the leaderboard measures, and a
    /// leaderboard nobody trusts is a retention feature that has stopped working. Characters and
    /// skins have identical stats. What money buys is expression, time, and the removal of friction.
    /// </summary>
    public sealed class StoreItem
    {
        public StoreItem(string id, string displayName, ItemKind kind, Price price)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id required.", nameof(id));

            Id = id;
            DisplayName = displayName ?? id;
            Kind = kind;
            Price = price;
        }

        /// <summary>
        /// Stable identifier, forever. Persisted in the save file and in the server's entitlement
        /// record, so it can never be renamed once a single player has bought the item.
        /// </summary>
        public string Id { get; }

        public string DisplayName { get; }

        public ItemKind Kind { get; }

        public Price Price { get; }

        /// <summary>Coins granted on purchase (currency packs and bundles).</summary>
        public int GrantsCoins { get; set; }

        /// <summary>Gems granted on purchase.</summary>
        public int GrantsGems { get; set; }

        /// <summary>Other items unlocked by this one (bundles).</summary>
        public IReadOnlyList<string> GrantsItems { get; set; } = Array.Empty<string>();

        /// <summary>
        /// True when this item can be bought repeatedly (currency packs).
        /// Cosmetics are one-shot: buying a skin you already own must be impossible, not merely
        /// discouraged, or a mis-tap costs a player 500 gems and costs you a support ticket.
        /// </summary>
        public bool IsConsumable => Kind == ItemKind.CurrencyPack;
    }

    /// <summary>
    /// Everything the store sells.
    ///
    /// In the shipping game this is populated from Remote Config so prices, discounts and offers can
    /// change without a store submission (ARCHITECTURE.md §9). The defaults compiled in here exist so
    /// that a player who launches offline still sees a complete, coherent store.
    /// </summary>
    public sealed class StoreCatalog
    {
        private readonly Dictionary<string, StoreItem> _items = new();

        public void Add(StoreItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (_items.ContainsKey(item.Id))
            {
                // A duplicate id means two different things claim the same entitlement key. Whichever
                // one the dictionary happened to keep is what the player owns — and which one that is
                // depends on load order. Fail loudly instead.
                throw new ArgumentException($"Duplicate store item id '{item.Id}'.", nameof(item));
            }

            _items[item.Id] = item;
        }

        public bool TryGet(string id, out StoreItem item) => _items.TryGetValue(id, out item);

        public IReadOnlyCollection<StoreItem> All => _items.Values;

        /// <summary>The compiled-in default catalogue. A complete, playable store with no network.</summary>
        public static StoreCatalog Default()
        {
            var catalog = new StoreCatalog();

            // --- Cosmetics, bought with soft currency. The coin sink that gives coins meaning.
            catalog.Add(new StoreItem("char_nova", "Nova", ItemKind.Character, Price.InCurrency(CurrencyType.Coins, 2_500)));
            catalog.Add(new StoreItem("char_rook", "Rook", ItemKind.Character, Price.InCurrency(CurrencyType.Coins, 7_500)));
            catalog.Add(new StoreItem("board_pulse", "Pulse Board", ItemKind.Board, Price.InCurrency(CurrencyType.Coins, 4_000)));

            // --- Premium cosmetics, bought with gems. The gem sink.
            catalog.Add(new StoreItem("skin_nova_void", "Nova — Void", ItemKind.Skin, Price.InCurrency(CurrencyType.Gems, 350)));
            catalog.Add(new StoreItem("board_prism", "Prism Board", ItemKind.Board, Price.InCurrency(CurrencyType.Gems, 500)));

            // --- Real money. These are the faucets.
            catalog.Add(new StoreItem("gems_100", "100 Gems", ItemKind.CurrencyPack,
                Price.InRealMoney("com.mooncatstudio.neonrush.gems_100")) { GrantsGems = 100 });

            catalog.Add(new StoreItem("gems_500", "500 Gems", ItemKind.CurrencyPack,
                Price.InRealMoney("com.mooncatstudio.neonrush.gems_500")) { GrantsGems = 500 });

            catalog.Add(new StoreItem("gems_1200", "1,200 Gems", ItemKind.CurrencyPack,
                Price.InRealMoney("com.mooncatstudio.neonrush.gems_1200")) { GrantsGems = 1_200 });

            catalog.Add(new StoreItem("gems_2500", "2,500 Gems", ItemKind.CurrencyPack,
                Price.InRealMoney("com.mooncatstudio.neonrush.gems_2500")) { GrantsGems = 2_500 });

            catalog.Add(new StoreItem("gems_5000", "5,000 Gems", ItemKind.CurrencyPack,
                Price.InRealMoney("com.mooncatstudio.neonrush.gems_5000")) { GrantsGems = 5_000 });

            // The starter bundle: the single highest-converting product in almost every runner. It is
            // priced as an obvious bargain because its job is not to earn much — it is to convert a
            // player from "never paid" to "has paid", which is the hardest and most valuable
            // transition in the entire funnel. Everything after the first purchase is easier.
            catalog.Add(new StoreItem("bundle_starter", "Starter Bundle", ItemKind.Bundle,
                Price.InRealMoney("com.mooncatstudio.neonrush.starter"))
            {
                GrantsGems = 500,
                GrantsCoins = 5_000,
                GrantsItems = new[] { "char_nova", "skin_nova_void" },
            });

            catalog.Add(new StoreItem("no_ads", "Remove Ads", ItemKind.AdRemoval,
                Price.InRealMoney("com.mooncatstudio.neonrush.noads")));

            return catalog;
        }
    }
}
