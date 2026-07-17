using System;
using NeonRush.Application.Store;
using NeonRush.Application.Subscription;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Store;
using NeonRush.Domain.Subscription;
using NeonRush.Presentation.Visuals;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The VIP subscription screen: benefits, status, subscribe, and the daily gem claim.
    ///
    /// Recurring revenue is the highest-LTV mechanic in the game, so this screen exists to make the
    /// value obvious and the daily perk sticky. The subscribe button drives the ordinary store
    /// purchase (vip_monthly) — same real-money, receipt-validated flow — and the rest reflects the
    /// live <see cref="SubscriptionService"/>. Grey-box uGUI, no shader of its own.
    /// </summary>
    public sealed class VipScreen : IDisposable
    {
        private readonly StoreCatalog _catalog;
        private readonly StoreService _store;
        private readonly IIapService _iap;
        private readonly SubscriptionService _vip;
        private readonly System.Collections.Generic.List<IDisposable> _subscriptions = new();

        private StoreItem _item;
        private GameObject _root;
        private Text _status;
        private Text _benefits;
        private Button _subscribeButton;
        private Text _subscribeLabel;
        private Button _claimButton;
        private Text _claimLabel;
        private Text _toast;

        public VipScreen(StoreCatalog catalog, StoreService store, IIapService iap, SubscriptionService vip,
            IEventBus bus, Transform uiRoot)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _iap = iap ?? throw new ArgumentNullException(nameof(iap));
            _vip = vip ?? throw new ArgumentNullException(nameof(vip));

            if (bus == null) throw new ArgumentNullException(nameof(bus));

            _catalog.TryGet(SubscriptionService.ProductItemId, out _item);

            Build(uiRoot);

            _subscriptions.Add(bus.Subscribe<SubscriptionActivated>(_ => RefreshIfOpen()));
            _subscriptions.Add(bus.Subscribe<CurrencyChanged>(_ => RefreshIfOpen()));

            Hide();
        }

        public bool IsOpen => _root != null && _root.activeSelf;

        public void Show()
        {
            _root.SetActive(true);
            SetToast(string.Empty);
            Refresh();
        }

        public void Hide() => _root.SetActive(false);

        private void RefreshIfOpen()
        {
            if (IsOpen) Refresh();
        }

        // -------------------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------------------

        private void Build(Transform uiRoot)
        {
            var canvasGo = new GameObject("Vip", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 14;

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
            go.GetComponent<Image>().color = new Color(0.01f, 0.01f, 0.04f, 0.93f);
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
            rect.sizeDelta = new Vector2(900f, 1140f);
            card.GetComponent<Image>().color = new Color(0.1f, 0.08f, 0.15f, 1f);

            var title = Label(card.transform, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -48f), TextAnchor.UpperCenter, 72);
            title.text = "★ VIP";
            title.color = NeonMaterials.Coin;

            _status = Label(card.transform, "Status", new Vector2(0.5f, 1f), new Vector2(0f, -150f), TextAnchor.UpperCenter, 38);
            _status.color = new Color(0.75f, 0.85f, 1f);

            _benefits = Label(card.transform, "Benefits", new Vector2(0.5f, 0.5f), new Vector2(0f, 70f), TextAnchor.MiddleCenter, 42);
            _benefits.rectTransform.sizeDelta = new Vector2(820f, 520f);
            _benefits.color = new Color(0.9f, 0.96f, 1f);

            // Claim-daily button (shown/enabled only while active).
            (_claimButton, _claimLabel) = BuildButton(card.transform, "CLAIM", OnClaimDaily, new Color(0.16f, 0.6f, 0.45f));
            var claimRect = _claimButton.GetComponent<RectTransform>();
            claimRect.anchorMin = new Vector2(0.5f, 0f);
            claimRect.anchorMax = new Vector2(0.5f, 0f);
            claimRect.pivot = new Vector2(0.5f, 0f);
            claimRect.sizeDelta = new Vector2(620f, 104f);
            claimRect.anchoredPosition = new Vector2(0f, 292f);

            // Subscribe button (real money).
            (_subscribeButton, _subscribeLabel) = BuildButton(card.transform, "SUBSCRIBE", OnSubscribe, new Color(0.55f, 0.35f, 0.85f));
            var subRect = _subscribeButton.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.5f, 0f);
            subRect.anchorMax = new Vector2(0.5f, 0f);
            subRect.pivot = new Vector2(0.5f, 0f);
            subRect.sizeDelta = new Vector2(620f, 120f);
            subRect.anchoredPosition = new Vector2(0f, 158f);

            _toast = Label(card.transform, "Toast", new Vector2(0.5f, 0f), new Vector2(0f, 118f), TextAnchor.LowerCenter, 28);
            _toast.color = NeonMaterials.Obstacle;

            var (closeBtn, closeLabel) = BuildButton(card.transform, "CLOSE", Hide, new Color(0.16f, 0.55f, 0.85f));
            closeLabel.text = "CLOSE";
            var closeRect = closeBtn.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.5f, 0f);
            closeRect.anchorMax = new Vector2(0.5f, 0f);
            closeRect.pivot = new Vector2(0.5f, 0f);
            closeRect.sizeDelta = new Vector2(360f, 74f);
            closeRect.anchoredPosition = new Vector2(0f, 36f);
        }

        // -------------------------------------------------------------------------------
        // Behaviour
        // -------------------------------------------------------------------------------

        private void OnSubscribe()
        {
            if (_item == null || _store.IsPurchaseInFlight) return;

            _subscribeLabel.text = "...";
            _subscribeButton.interactable = false;

            _store.PurchaseWithMoney(_item.Id, outcome =>
            {
                // On success the subscription extends via SubscriptionService reacting to
                // PurchaseCompleted, and the SubscriptionActivated event refreshes this screen.
                if (!outcome.Succeeded)
                {
                    SetToast(outcome.Failure switch
                    {
                        PurchaseFailure.Cancelled => "Cancelled",
                        PurchaseFailure.ReceiptRejected => "Could not verify purchase",
                        PurchaseFailure.StoreUnavailable => "Store unavailable",
                        _ => "Purchase failed",
                    });
                }
                else
                {
                    SetToast("Welcome to VIP!");
                }

                Refresh();
            });
        }

        private void OnClaimDaily()
        {
            var gems = _vip.ClaimDailyGems();
            SetToast(gems > 0 ? $"+{gems} gems" : "Come back tomorrow");
            Refresh();
        }

        // -------------------------------------------------------------------------------
        // Refresh
        // -------------------------------------------------------------------------------

        private void Refresh()
        {
            var active = _vip.IsActive;

            _status.text = active
                ? $"ACTIVE — {DaysLeft()} left"
                : "not a member yet";
            _status.color = active ? new Color(0.4f, 0.9f, 0.6f) : new Color(0.7f, 0.75f, 0.85f);

            _benefits.text =
                $"{Multiplier()}×  coins on every run\n\n" +
                $"{_vip.DailyGems:N0}  gems, every day\n\n" +
                "no interstitial ads";

            // Subscribe button: price for a new sub, "renew" wording once active.
            _subscribeButton.interactable = true;
            var price = _item != null ? _iap.GetLocalisedPrice(_item.Price.ProductId) : null;
            var verb = active ? "RENEW" : "SUBSCRIBE";
            _subscribeLabel.text = price != null ? $"{verb}  —  {price}" : verb;

            // Claim button: only meaningful while active.
            if (!active)
            {
                _claimButton.interactable = false;
                _claimLabel.text = "daily gems (VIP only)";
                return;
            }

            var available = _vip.DailyGemsAvailable;
            _claimButton.interactable = available;
            _claimLabel.text = available ? $"CLAIM  +{_vip.DailyGems:N0} GEMS" : "gems claimed today";
        }

        private string DaysLeft()
        {
            var days = (int)Math.Ceiling(_vip.Remaining.TotalDays);
            return days == 1 ? "1 day" : $"{days} days";
        }

        private string Multiplier()
        {
            var m = _vip.CoinMultiplier;
            // Trim a whole number to "2" rather than "2.0".
            return Math.Abs(m - Math.Round(m)) < 0.01f ? ((int)Math.Round(m)).ToString() : m.ToString("0.#");
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

            var text = Label(go.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 38);
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
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
