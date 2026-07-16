using System;
using System.Collections.Generic;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;
using NeonRush.Presentation.Visuals;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The store, built entirely in code.
    ///
    /// Same choice as the HUD, for the same reasons: a store authored as a prefab is a binary blob
    /// full of GUIDs that cannot be reviewed in a diff and breaks silently when a reference goes
    /// missing. This one is diffable, and its wiring is compile-checked. It is unapologetically
    /// grey-box — real icons, animation and layout come with the art pass — but every interaction
    /// (buy, insufficient funds, already-owned, real-money spinner) is real and exercises the same
    /// StoreService the shipping game uses.
    ///
    /// One important integration detail lives here: prices for real-money items come from the
    /// platform store's own localised string (<see cref="IIapService.GetLocalisedPrice"/>), never
    /// formatted from a number. Format it yourself and you will get the currency symbol, the decimal
    /// separator, or the tax-inclusive rounding wrong for some country, and the price the player sees
    /// will not match what they are charged — which is both a support nightmare and, in several
    /// jurisdictions, illegal.
    /// </summary>
    public sealed class StoreScreen : IDisposable
    {
        private readonly StoreCatalog _catalog;
        private readonly StoreService _store;
        private readonly Wallet _wallet;
        private readonly Inventory _inventory;
        private readonly IIapService _iap;
        private readonly List<IDisposable> _subscriptions = new();

        private GameObject _root;
        private Text _coinBalance;
        private Text _gemBalance;
        private Text _toast;
        private readonly List<Card> _cards = new();

        private const float CardHeight = 150f;
        private const float CardGap = 12f;

        /// <summary>One item's row: the widgets that need updating after a purchase.</summary>
        private sealed class Card
        {
            public StoreItem Item;
            public Button Buy;
            public Text BuyLabel;
            public Text Status;
        }

        public StoreScreen(
            StoreCatalog catalog,
            StoreService store,
            Wallet wallet,
            Inventory inventory,
            IIapService iap,
            IEventBus bus,
            Transform uiRoot)
        {
            _catalog = catalog;
            _store = store;
            _wallet = wallet;
            _inventory = inventory;
            _iap = iap;

            Build(uiRoot);

            // The balance header is event-driven. A purchase, a run reward, or an ad grant all move
            // it, and it must stay correct while the store is open or the player watches their gems
            // fail to drop after a purchase and assumes the buy did not work.
            _subscriptions.Add(bus.Subscribe<CurrencyChanged>(_ => RefreshBalances()));

            Hide();
        }

        public bool IsOpen => _root != null && _root.activeSelf;

        public void Show()
        {
            _root.SetActive(true);
            SetToast(string.Empty);
            RefreshBalances();
            RefreshCards();
        }

        public void Hide() => _root.SetActive(false);

        // -------------------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------------------

        private void Build(Transform uiRoot)
        {
            var canvasGo = new GameObject("Store", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Above the HUD. The HUD's canvas is default order 0; the store must sit on top of it so
            // the run's score is not showing through the shop.
            canvas.sortingOrder = 10;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0f;

            _root = canvasGo;

            BuildDimmer(canvasGo.transform);
            BuildHeader(canvasGo.transform);
            BuildScrollList(canvasGo.transform);
            BuildToast(canvasGo.transform);
            BuildCloseButton(canvasGo.transform);
        }

        /// <summary>A full-screen dark panel that also swallows taps meant for the game behind it.</summary>
        private static void BuildDimmer(Transform parent)
        {
            var go = new GameObject("Dimmer", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);

            Stretch(go.GetComponent<RectTransform>());

            var image = go.GetComponent<Image>();
            image.color = new Color(0.01f, 0.01f, 0.05f, 0.94f);

            // raycastTarget stays TRUE. That is deliberate — it is what stops a tap on the store's
            // background from falling through to the game-over screen and restarting the run.
            image.raycastTarget = true;
        }

        private void BuildHeader(Transform parent)
        {
            var title = Label(parent, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -60f), TextAnchor.UpperCenter, 64);
            title.text = "STORE";
            title.color = NeonMaterials.Coin;

            _coinBalance = Label(parent, "Coins", new Vector2(0f, 1f), new Vector2(50f, -150f), TextAnchor.UpperLeft, 40);
            _gemBalance = Label(parent, "Gems", new Vector2(1f, 1f), new Vector2(-50f, -150f), TextAnchor.UpperRight, 40);
        }

        private void BuildScrollList(Transform parent)
        {
            // Viewport: the visible window, with a RectMask2D so cards scroll under the header
            // instead of overlapping it. RectMask2D needs no material and is the cheap masking path.
            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(parent, worldPositionStays: false);

            var viewport = viewportGo.GetComponent<RectTransform>();
            viewport.anchorMin = new Vector2(0.5f, 0.5f);
            viewport.anchorMax = new Vector2(0.5f, 0.5f);
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.sizeDelta = new Vector2(960f, 1180f);
            viewport.anchoredPosition = new Vector2(0f, -30f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewport, worldPositionStays: false);

            var content = contentGo.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(0f, 0f);

            var items = new List<StoreItem>(_catalog.All);

            // Stable display order: currency packs first (the faucets the player came for), then
            // cosmetics. Sorting by an explicit key rather than relying on dictionary order keeps the
            // shop from reshuffling itself between launches, which reads as jank.
            items.Sort((a, b) => DisplayRank(a).CompareTo(DisplayRank(b)));

            var totalHeight = items.Count * (CardHeight + CardGap) + CardGap;
            content.sizeDelta = new Vector2(0f, totalHeight);

            for (var i = 0; i < items.Count; i++)
            {
                _cards.Add(BuildCard(content, items[i], i));
            }

            var scroll = viewportGo.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
        }

        private static int DisplayRank(StoreItem item) => item.Kind switch
        {
            ItemKind.CurrencyPack => 0,
            ItemKind.Bundle => 1,
            ItemKind.AdRemoval => 2,
            _ => 3,
        };

        private Card BuildCard(Transform content, StoreItem item, int index)
        {
            var cardGo = new GameObject($"Card_{item.Id}", typeof(RectTransform), typeof(Image));
            cardGo.transform.SetParent(content, worldPositionStays: false);

            var rect = cardGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(20f, 0f);
            rect.offsetMax = new Vector2(-20f, 0f);
            rect.sizeDelta = new Vector2(0f, CardHeight);
            rect.anchoredPosition = new Vector2(0f, -(CardGap + index * (CardHeight + CardGap)));

            cardGo.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.16f, 0.95f);

            var name = Label(cardGo.transform, "Name", new Vector2(0f, 1f), new Vector2(30f, -22f), TextAnchor.UpperLeft, 38);
            name.text = item.DisplayName;

            var status = Label(cardGo.transform, "Status", new Vector2(0f, 0f), new Vector2(30f, 24f), TextAnchor.LowerLeft, 28);
            status.color = new Color(0.6f, 0.7f, 0.85f);

            var (buy, buyLabel) = BuildButton(cardGo.transform, string.Empty, () => { });
            var buyRect = buy.GetComponent<RectTransform>();
            buyRect.anchorMin = new Vector2(1f, 0.5f);
            buyRect.anchorMax = new Vector2(1f, 0.5f);
            buyRect.pivot = new Vector2(1f, 0.5f);
            buyRect.sizeDelta = new Vector2(300f, 96f);
            buyRect.anchoredPosition = new Vector2(-30f, 0f);

            var card = new Card { Item = item, Buy = buy, BuyLabel = buyLabel, Status = status };

            // Capture the item, not the loop index — a classic closure trap that would make every
            // button buy the last item in the list.
            buy.onClick.AddListener(() => OnBuy(card));

            return card;
        }

        private void BuildToast(Transform parent)
        {
            _toast = Label(parent, "Toast", new Vector2(0.5f, 0f), new Vector2(0f, 210f), TextAnchor.LowerCenter, 34);
            _toast.color = NeonMaterials.Obstacle;
        }

        private void BuildCloseButton(Transform parent)
        {
            var (button, label) = BuildButton(parent, "CLOSE", Hide);
            label.text = "CLOSE";

            var rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(400f, 110f);
            rect.anchoredPosition = new Vector2(0f, 70f);
        }

        // -------------------------------------------------------------------------------
        // Purchase flow
        // -------------------------------------------------------------------------------

        private void OnBuy(Card card)
        {
            if (_store.IsPurchaseInFlight) return;

            var item = card.Item;

            if (item.Price.IsRealMoney)
            {
                // Real money is asynchronous — the store, then the server. Lock the button and show a
                // pending state so the player cannot double-tap and start two purchases.
                card.BuyLabel.text = "...";
                card.Buy.interactable = false;

                _store.PurchaseWithMoney(item.Id, outcome =>
                {
                    ShowOutcome(outcome);
                    RefreshCards();
                });

                return;
            }

            // Currency is local, so this resolves on the spot.
            var result = _store.PurchaseWithCurrency(item.Id);
            ShowOutcome(result);
            RefreshCards();
        }

        private void ShowOutcome(PurchaseOutcome outcome)
        {
            if (outcome.Succeeded)
            {
                SetToast("Purchased!", NeonMaterials.Player);
                return;
            }

            SetToast(outcome.Failure switch
            {
                PurchaseFailure.InsufficientFunds => "Not enough currency",
                PurchaseFailure.AlreadyOwned => "Already owned",
                PurchaseFailure.Cancelled => "Cancelled",
                PurchaseFailure.ReceiptRejected => "Purchase could not be verified",
                PurchaseFailure.StoreUnavailable => "Store unavailable",
                _ => "Purchase failed",
            }, NeonMaterials.Obstacle);
        }

        // -------------------------------------------------------------------------------
        // Refresh
        // -------------------------------------------------------------------------------

        private void RefreshBalances()
        {
            if (_coinBalance != null) _coinBalance.text = $"COINS  {_wallet.Balance(CurrencyType.Coins):N0}";
            if (_gemBalance != null) _gemBalance.text = $"GEMS  {_wallet.Balance(CurrencyType.Gems):N0}";
        }

        private void RefreshCards()
        {
            foreach (var card in _cards)
            {
                var item = card.Item;
                var owned = !item.IsConsumable && _inventory.Has(item.Id);

                if (owned)
                {
                    card.Buy.interactable = false;
                    card.BuyLabel.text = "OWNED";
                    card.Status.text = "in your collection";
                    continue;
                }

                if (item.Price.IsRealMoney)
                {
                    // The store's own localised string, never a self-formatted number. Falls back to
                    // a placeholder when the store has not initialised (offline), so the card still
                    // renders instead of showing an empty button.
                    var price = _iap.GetLocalisedPrice(item.Price.ProductId);
                    card.BuyLabel.text = price ?? "BUY";
                    card.Status.text = DescribeGrants(item);
                    card.Buy.interactable = true;
                }
                else
                {
                    var currency = item.Price.Currency == CurrencyType.Coins ? "COINS" : "GEMS";
                    card.BuyLabel.text = $"{item.Price.Amount:N0}\n{currency}";
                    card.Status.text = DescribeGrants(item);

                    // Grey the button when they cannot afford it — but the buy still *works* and
                    // reports the shortfall, which is the signal a targeted offer is built from. A
                    // disabled button hides that intent; here we merely dim it.
                    card.Buy.interactable = _wallet.CanAfford(item.Price.Currency, item.Price.Amount);
                }
            }
        }

        private static string DescribeGrants(StoreItem item)
        {
            var parts = new List<string>();
            if (item.GrantsGems > 0) parts.Add($"+{item.GrantsGems:N0} gems");
            if (item.GrantsCoins > 0) parts.Add($"+{item.GrantsCoins:N0} coins");
            if (item.Kind == ItemKind.AdRemoval) parts.Add("removes interstitial ads");
            if (item.Kind is ItemKind.Character or ItemKind.Skin or ItemKind.Board) parts.Add("cosmetic");

            return parts.Count > 0 ? string.Join("  ·  ", parts) : item.Kind.ToString();
        }

        // -------------------------------------------------------------------------------
        // Widgets
        // -------------------------------------------------------------------------------

        private void SetToast(string message, Color? colour = null)
        {
            _toast.text = message;
            if (colour.HasValue) _toast.color = colour.Value;
        }

        private static (Button, Text) BuildButton(Transform parent, string label, Action onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, worldPositionStays: false);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.16f, 0.55f, 0.85f);

            var button = go.GetComponent<Button>();

            // A visible disabled state. Without it, a greyed "cannot afford" button looks identical
            // to an active one and the player taps a dead button wondering why nothing happens.
            var colours = button.colors;
            colours.disabledColor = new Color(0.22f, 0.24f, 0.3f);
            colours.highlightedColor = new Color(0.25f, 0.68f, 1f);
            colours.pressedColor = new Color(0.1f, 0.4f, 0.65f);
            button.colors = colours;

            button.onClick.AddListener(() => onClick());

            var text = Label(go.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 34);
            text.text = label;
            text.color = Color.white;
            Stretch(text.GetComponent<RectTransform>());

            return (button, text);
        }

        private static Text Label(Transform parent, string name, Vector2 anchor, Vector2 offset, TextAnchor alignment, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, worldPositionStays: false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(640f, 120f);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = alignment;
            text.color = new Color(0.9f, 0.96f, 1f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // Labels must not eat clicks meant for the button underneath them.
            text.raycastTarget = false;

            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
