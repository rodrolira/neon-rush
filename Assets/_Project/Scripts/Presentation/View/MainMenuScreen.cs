using System;
using System.Collections.Generic;
using NeonRush.Application.Missions;
using NeonRush.Application.Progression;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Retention;
using NeonRush.Presentation.Visuals;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The main menu: title, balances, best score, today's missions, the daily-reward claim, and
    /// the way into a run.
    ///
    /// Same construction philosophy as the HUD and the store — built in code, grey-box, every
    /// interaction real. Two design points carry weight here:
    ///
    ///  · <b>The daily reward is an explicit claim, not an automatic grant.</b> The claim is the
    ///    ritual: the player opens the app, sees "DAY 4 — +300 COINS", taps, watches the bank jump.
    ///    That ten-second ceremony is most of the retention value of the feature; an auto-grant at
    ///    boot delivers the coins and throws away the reason they came. (It was auto-granted before
    ///    this screen existed, because a reward that silently expired behind a missing UI would have
    ///    been worse. Now the UI exists.)
    ///
    ///  · <b>Starting a run is a tap anywhere, not a small button.</b> The menu's job is to be left.
    ///    Missions and the shop are glanceable on the way through, but the dominant affordance must
    ///    be "play NOW" — a runner that makes you hunt for the play button loses sessions at the
    ///    front door.
    /// </summary>
    public sealed class MainMenuScreen : IDisposable
    {
        private readonly Wallet _wallet;
        private readonly PlayerProfile _profile;
        private readonly MissionService _missions;
        private readonly DailyRewardService _daily;
        private readonly List<IDisposable> _subscriptions = new();

        private GameObject _root;
        private Text _coins;
        private Text _gems;
        private Text _bestScore;
        private Text _streak;
        private readonly List<Text> _missionRows = new();

        private GameObject _claimButton;
        private Text _claimLabel;

        /// <summary>Raised when the player wants to start a run.</summary>
        public event Action StartRequested;

        /// <summary>Raised when the player opens the shop.</summary>
        public event Action ShopRequested;

        /// <summary>Raised when the player opens the battle pass.</summary>
        public event Action PassRequested;

        public MainMenuScreen(
            Wallet wallet,
            PlayerProfile profile,
            MissionService missions,
            DailyRewardService daily,
            IEventBus bus,
            Transform uiRoot)
        {
            _wallet = wallet;
            _profile = profile;
            _missions = missions;
            _daily = daily;

            Build(uiRoot);

            // Balances stay live while the menu is open (a daily claim moves them, and the player is
            // looking straight at the number when it happens — that is the whole point).
            _subscriptions.Add(bus.Subscribe<CurrencyChanged>(_ => RefreshBalances()));

            Hide();
        }

        public bool IsOpen => _root != null && _root.activeSelf;

        public void Show()
        {
            _root.SetActive(true);
            RefreshBalances();
            RefreshMissions();
            RefreshDaily();
        }

        public void Hide() => _root.SetActive(false);

        // -------------------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------------------

        private void Build(Transform uiRoot)
        {
            var canvasGo = new GameObject("MainMenu", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Above the HUD (0), below the store (10): opening the shop from the menu must cover it.
            canvas.sortingOrder = 5;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0f;

            _root = canvasGo;

            // Full-screen backdrop that IS the play button. Panels and buttons stack on top and
            // swallow their own clicks; a tap anywhere else starts the run — the dominant affordance.
            var backdrop = new GameObject("TapToRun", typeof(RectTransform), typeof(Image), typeof(Button));
            backdrop.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            Stretch(backdrop.GetComponent<RectTransform>());
            backdrop.GetComponent<Image>().color = new Color(0.01f, 0.01f, 0.06f, 0.92f);
            backdrop.GetComponent<Button>().onClick.AddListener(() => StartRequested?.Invoke());

            BuildHeader(canvasGo.transform);
            BuildMissionsPanel(canvasGo.transform);
            BuildDailyPanel(canvasGo.transform);
            BuildFooter(canvasGo.transform);
        }

        private void BuildHeader(Transform parent)
        {
            var title = Label(parent, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -140f), TextAnchor.UpperCenter, 96);
            title.text = "NEON RUSH";
            title.color = NeonMaterials.Player;

            _bestScore = Label(parent, "Best", new Vector2(0.5f, 1f), new Vector2(0f, -270f), TextAnchor.UpperCenter, 40);
            _bestScore.color = new Color(0.7f, 0.8f, 0.95f);

            _coins = Label(parent, "Coins", new Vector2(0f, 1f), new Vector2(50f, -60f), TextAnchor.UpperLeft, 38);
            _gems = Label(parent, "Gems", new Vector2(1f, 1f), new Vector2(-50f, -60f), TextAnchor.UpperRight, 38);
        }

        private void BuildMissionsPanel(Transform parent)
        {
            var panel = Panel(parent, "Missions", new Vector2(0.5f, 0.5f), new Vector2(0f, 130f), new Vector2(920f, 360f));

            var header = Label(panel.transform, "Header", new Vector2(0.5f, 1f), new Vector2(0f, -18f), TextAnchor.UpperCenter, 36);
            header.text = "TODAY'S MISSIONS";
            header.color = NeonMaterials.Coin;

            for (var i = 0; i < MissionService.ActiveCount; i++)
            {
                var row = Label(panel.transform, $"Mission{i}", new Vector2(0f, 1f), new Vector2(40f, -90f - i * 80f), TextAnchor.UpperLeft, 32);
                row.rectTransform.sizeDelta = new Vector2(840f, 70f);
                _missionRows.Add(row);
            }
        }

        private void BuildDailyPanel(Transform parent)
        {
            var panel = Panel(parent, "Daily", new Vector2(0.5f, 0.5f), new Vector2(0f, -230f), new Vector2(920f, 210f));

            _streak = Label(panel.transform, "Streak", new Vector2(0.5f, 1f), new Vector2(0f, -18f), TextAnchor.UpperCenter, 34);

            _claimButton = new GameObject("Claim", typeof(RectTransform), typeof(Image), typeof(Button));
            _claimButton.transform.SetParent(panel.transform, worldPositionStays: false);

            var rect = _claimButton.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(520f, 96f);
            rect.anchoredPosition = new Vector2(0f, 22f);

            _claimButton.GetComponent<Image>().color = new Color(0.85f, 0.62f, 0.1f);
            _claimButton.GetComponent<Button>().onClick.AddListener(OnClaimDaily);

            _claimLabel = Label(_claimButton.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 36);
            _claimLabel.color = Color.white;
            Stretch(_claimLabel.rectTransform);
        }

        private void BuildFooter(Transform parent)
        {
            // Two buttons side by side: SHOP (spend) and PASS (the season's rewards). Neither is the
            // dominant affordance — that is the tap-anywhere backdrop — but both are one glance away.
            BuildFooterButton(parent, "Shop", "SHOP", new Vector2(-210f, 70f),
                new Color(0.55f, 0.35f, 0.85f), () => ShopRequested?.Invoke());

            BuildFooterButton(parent, "Pass", "PASS", new Vector2(210f, 70f),
                new Color(0.16f, 0.6f, 0.45f), () => PassRequested?.Invoke());

            var hint = Label(parent, "Hint", new Vector2(0.5f, 0f), new Vector2(0f, 200f), TextAnchor.LowerCenter, 30);
            hint.text = "tap anywhere to run";
            hint.color = new Color(0.55f, 0.65f, 0.8f);
        }

        private void BuildFooterButton(Transform parent, string name, string caption, Vector2 position, Color colour, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, worldPositionStays: false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(380f, 110f);
            rect.anchoredPosition = position;

            go.GetComponent<Image>().color = colour;
            go.GetComponent<Button>().onClick.AddListener(() => onClick());

            var label = Label(go.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 38);
            label.text = caption;
            label.color = Color.white;
            Stretch(label.rectTransform);
        }

        // -------------------------------------------------------------------------------
        // Behaviour
        // -------------------------------------------------------------------------------

        private void OnClaimDaily()
        {
            if (_daily.TryClaim(out var claimed) != ClaimRefusal.None) return;

            // The bank numbers jump via the CurrencyChanged subscription while the player watches —
            // the payoff moment. The button then flips to the "come back tomorrow" state.
            _claimLabel.text = $"+{claimed.CoinsGranted} COINS" +
                               (claimed.GemsGranted > 0 ? $"  +{claimed.GemsGranted} GEMS" : string.Empty);

            RefreshDaily();
        }

        private void RefreshBalances()
        {
            if (_coins == null) return;

            _coins.text = $"COINS  {_wallet.Balance(CurrencyType.Coins):N0}";
            _gems.text = $"GEMS  {_wallet.Balance(CurrencyType.Gems):N0}";
            _bestScore.text = _profile.BestScore > 0 ? $"BEST  {_profile.BestScore:N0}" : "make your first run";
        }

        private void RefreshMissions()
        {
            var active = _missions.Active;

            for (var i = 0; i < _missionRows.Count; i++)
            {
                if (i >= active.Count)
                {
                    _missionRows[i].text = string.Empty;
                    continue;
                }

                var mission = active[i];

                _missionRows[i].text = mission.Rewarded
                    ? $"✓ {mission.Definition.Description}  (+{mission.Definition.RewardCoins})"
                    : $"{mission.Definition.Description}  —  {mission.Progress}/{mission.Definition.Target}";

                _missionRows[i].color = mission.Rewarded
                    ? new Color(0.4f, 0.9f, 0.6f)
                    : new Color(0.85f, 0.9f, 1f);
            }
        }

        private void RefreshDaily()
        {
            var availability = _daily.Availability();

            if (availability == ClaimRefusal.None)
            {
                var (coins, gems) = _daily.NextReward;

                _streak.text = $"DAILY REWARD — DAY {_daily.NextStreakDay}";
                _claimLabel.text = $"CLAIM  +{coins:N0} COINS" + (gems > 0 ? $"  +{gems} GEMS" : string.Empty);
                _claimButton.SetActive(true);

                return;
            }

            _claimButton.SetActive(false);

            _streak.text = availability == ClaimRefusal.AlreadyClaimedToday
                ? $"STREAK: {_daily.StreakDays} {(_daily.StreakDays == 1 ? "DAY" : "DAYS")} — come back tomorrow"
                : "daily reward unavailable"; // TimeInconsistent: the clock is being sorted out.
        }

        // -------------------------------------------------------------------------------
        // Widgets
        // -------------------------------------------------------------------------------

        private static GameObject Panel(Transform parent, string name, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.color = new Color(0.07f, 0.08f, 0.15f, 0.96f);

            // The panel swallows taps so glancing at missions does not launch a run.
            image.raycastTarget = true;

            return go;
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
            rect.sizeDelta = new Vector2(700f, 90f);

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
