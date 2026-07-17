using System;
using System.Collections.Generic;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;
using NeonRush.Presentation.Visuals;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The starter-pack offer popup: the timed, one-shot bargain that converts a player from "never
    /// paid" to "has paid".
    ///
    /// It sells the catalogue's <c>bundle_starter</c> through the ordinary <see cref="StoreService"/>,
    /// so the purchase is the same real-money, receipt-validated flow as the shop. What this screen
    /// adds is presentation and pressure: the bundle's contents laid out as an obvious pile of value,
    /// a live countdown, and a single BUY button — shown at a high-intent moment rather than buried in
    /// a list. Grey-box uGUI, no shader of its own.
    /// </summary>
    public sealed class StarterPackOfferScreen : IDisposable
    {
        private readonly StoreCatalog _catalog;
        private readonly StoreService _store;
        private readonly IIapService _iap;
        private readonly StarterPackService _pack;

        private StoreItem _item;
        private GameObject _root;
        private Text _countdown;
        private Button _buyButton;
        private Text _buyLabel;
        private Text _toast;

        /// <summary>Raised when the popup closes (bought or dismissed), so the composition can resume.</summary>
        public event Action Closed;

        public StarterPackOfferScreen(StoreCatalog catalog, StoreService store, IIapService iap,
            StarterPackService pack, Transform uiRoot)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _iap = iap ?? throw new ArgumentNullException(nameof(iap));
            _pack = pack ?? throw new ArgumentNullException(nameof(pack));

            _catalog.TryGet(StarterPackService.BundleItemId, out _item);

            Build(uiRoot);
            Hide();
        }

        public bool IsOpen => _root != null && _root.activeSelf;

        public void Show()
        {
            if (_item == null) return; // catalogue without the bundle: nothing to show, fail safe

            _root.SetActive(true);
            SetToast(string.Empty);
            RefreshBuyButton();
            Tick(); // paint the countdown immediately rather than a frame late
        }

        public void Hide()
        {
            _root.SetActive(false);
        }

        /// <summary>Updates the countdown once per frame while open. Called from the game loop.</summary>
        public void Tick()
        {
            if (!IsOpen) return;

            _countdown.text = $"ENDS IN  {FormatCountdown(_pack.RemainingSeconds())}";
        }

        // -------------------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------------------

        private void Build(Transform uiRoot)
        {
            var canvasGo = new GameObject("StarterPack", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20; // above everything — this is a modal offer

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;

            _root = canvasGo;

            BuildDimmer(canvasGo.transform);
            BuildCard(canvasGo.transform);
        }

        private static void BuildDimmer(Transform parent)
        {
            var go = new GameObject("Dimmer", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            Stretch(go.GetComponent<RectTransform>());
            go.GetComponent<Image>().color = new Color(0.0f, 0.0f, 0.02f, 0.9f);
            go.GetComponent<Image>().raycastTarget = true;
        }

        private void BuildCard(Transform parent)
        {
            var card = new GameObject("Card", typeof(RectTransform), typeof(Image));
            card.transform.SetParent(parent, worldPositionStays: false);

            var rect = card.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(900f, 1180f);
            card.GetComponent<Image>().color = new Color(0.09f, 0.07f, 0.16f, 1f);

            var badge = Label(card.transform, "Badge", new Vector2(0.5f, 1f), new Vector2(0f, -40f), TextAnchor.UpperCenter, 34);
            badge.text = "ONE-TIME OFFER";
            badge.color = NeonMaterials.Obstacle;

            var title = Label(card.transform, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -100f), TextAnchor.UpperCenter, 72);
            title.text = "STARTER PACK";
            title.color = NeonMaterials.Coin;

            _countdown = Label(card.transform, "Countdown", new Vector2(0.5f, 1f), new Vector2(0f, -196f), TextAnchor.UpperCenter, 36);
            _countdown.color = new Color(1f, 0.8f, 0.4f);

            var contents = Label(card.transform, "Contents", new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), TextAnchor.MiddleCenter, 44);
            contents.rectTransform.sizeDelta = new Vector2(800f, 560f);
            contents.text = BuildContents();
            contents.color = new Color(0.9f, 0.96f, 1f);

            var value = Label(card.transform, "Value", new Vector2(0.5f, 0.5f), new Vector2(0f, -260f), TextAnchor.MiddleCenter, 30);
            value.text = "everything you need to get started — once only";
            value.color = new Color(0.6f, 0.7f, 0.85f);

            // Buy button (real money — the store's own localised price string).
            var (buyBtn, buyLabel) = BuildButton(card.transform, "BUY", OnBuy, new Color(0.16f, 0.62f, 0.42f));
            _buyButton = buyBtn;
            _buyLabel = buyLabel;
            var buyRect = _buyButton.GetComponent<RectTransform>();
            buyRect.anchorMin = new Vector2(0.5f, 0f);
            buyRect.anchorMax = new Vector2(0.5f, 0f);
            buyRect.pivot = new Vector2(0.5f, 0f);
            buyRect.sizeDelta = new Vector2(620f, 128f);
            buyRect.anchoredPosition = new Vector2(0f, 150f);

            _toast = Label(card.transform, "Toast", new Vector2(0.5f, 0f), new Vector2(0f, 108f), TextAnchor.LowerCenter, 28);
            _toast.color = NeonMaterials.Obstacle;

            // A quiet dismiss. Deliberately understated next to BUY — the offer stays available until
            // it expires, so "no thanks" costs the player nothing and is not a hard no.
            var (dismissBtn, dismissLabel) = BuildButton(card.transform, "no thanks", OnDismiss, new Color(0.18f, 0.19f, 0.26f));
            dismissLabel.text = "no thanks";
            var dRect = dismissBtn.GetComponent<RectTransform>();
            dRect.anchorMin = new Vector2(0.5f, 0f);
            dRect.anchorMax = new Vector2(0.5f, 0f);
            dRect.pivot = new Vector2(0.5f, 0f);
            dRect.sizeDelta = new Vector2(360f, 74f);
            dRect.anchoredPosition = new Vector2(0f, 40f);
        }

        private string BuildContents()
        {
            var lines = new List<string>();

            if (_item.GrantsGems > 0) lines.Add($"{_item.GrantsGems:N0} gems");
            if (_item.GrantsCoins > 0) lines.Add($"{_item.GrantsCoins:N0} coins");

            foreach (var id in _item.GrantsItems)
            {
                lines.Add(_catalog.TryGet(id, out var granted) ? granted.DisplayName : id);
            }

            return string.Join("\n", lines);
        }

        // -------------------------------------------------------------------------------
        // Behaviour
        // -------------------------------------------------------------------------------

        private void OnBuy()
        {
            if (_store.IsPurchaseInFlight) return;

            _buyLabel.text = "...";
            _buyButton.interactable = false;

            _store.PurchaseWithMoney(_item.Id, outcome =>
            {
                if (outcome.Succeeded)
                {
                    // The bundle's grants (gems, coins, cosmetics) are applied by StoreService; the
                    // offer is now owned, so ShouldOffer() will never return true again. Close out.
                    Close();
                    return;
                }

                SetToast(outcome.Failure switch
                {
                    PurchaseFailure.Cancelled => "Cancelled",
                    PurchaseFailure.ReceiptRejected => "Could not verify purchase",
                    PurchaseFailure.StoreUnavailable => "Store unavailable",
                    PurchaseFailure.AlreadyOwned => "Already owned",
                    _ => "Purchase failed",
                });

                RefreshBuyButton();
            });
        }

        private void OnDismiss() => Close();

        private void Close()
        {
            Hide();
            Closed?.Invoke();
        }

        private void RefreshBuyButton()
        {
            var price = _iap.GetLocalisedPrice(_item.Price.ProductId);
            _buyLabel.text = price != null ? $"GET IT  —  {price}" : "GET IT";
            _buyButton.interactable = true;
        }

        private static string FormatCountdown(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0d, seconds));

            if (ts.TotalDays >= 1d) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1d) return $"{ts.Hours}h {ts.Minutes}m";
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        // -------------------------------------------------------------------------------
        // Widgets
        // -------------------------------------------------------------------------------

        private void SetToast(string message) => _toast.text = message;

        private static (Button, Text) BuildButton(Transform parent, string label, Action onClick, Color colour)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, worldPositionStays: false);

            go.GetComponent<Image>().color = colour;

            var button = go.GetComponent<Button>();
            var colours = button.colors;
            colours.disabledColor = new Color(0.2f, 0.22f, 0.28f);
            button.colors = colours;
            button.onClick.AddListener(() => onClick());

            var text = Label(go.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 40);
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
            rect.sizeDelta = new Vector2(760f, 120f);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = alignment;
            text.color = new Color(0.9f, 0.96f, 1f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
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
        }
    }
}
