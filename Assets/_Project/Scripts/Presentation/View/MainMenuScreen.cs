using System;
using System.Collections.Generic;
using NeonRush.Application.Missions;
using NeonRush.Application.Progression;
using NeonRush.Application.Stages;
using NeonRush.Core.Events;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Retention;
using NeonRush.Presentation.Visuals;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The main menu: title, balances, best score, today's missions, the daily-reward claim, and the
    /// way into a run.
    ///
    /// Built in code like the rest of the UI, but styled to read as a real mobile game rather than a
    /// grey-box: rounded panels and buttons (from one procedurally-generated 9-slice sprite, so there
    /// is still no imported art and nothing to strip on device), currency chips, drop shadows for
    /// depth, and a single unmissable PLAY button as the focal point. Two design points still carry
    /// weight:
    ///
    ///  · <b>The daily reward is an explicit claim, not an automatic grant.</b> The claim is the
    ///    ritual — open the app, see "DAY 4 — +300 COINS", tap, watch the bank jump. That ceremony is
    ///    most of the feature's retention value.
    ///
    ///  · <b>Starting a run stays a tap almost anywhere.</b> The whole backdrop launches a run, and the
    ///    big PLAY button is the visual anchor for it; the menu's job is to be left quickly.
    /// </summary>
    public sealed class MainMenuScreen : IDisposable
    {
        private readonly Wallet _wallet;
        private readonly PlayerProfile _profile;
        private readonly MissionService _missions;
        private readonly DailyRewardService _daily;
        private readonly StageService _stages;
        private readonly List<IDisposable> _subscriptions = new();

        private GameObject _root;
        private Sprite _round;
        private Text _coins;
        private Text _gems;
        private Text _bestScore;
        private Text _streak;
        private readonly List<Text> _missionRows = new();

        private GameObject _stagePanel;
        private Text _stageTitle;
        private Text _stageReward;
        private readonly List<Text> _stageRows = new();

        private GameObject _claimButton;
        private Text _claimLabel;

        /// <summary>Raised when the player wants to start a run.</summary>
        public event Action StartRequested;

        /// <summary>Raised when the player opens the shop.</summary>
        public event Action ShopRequested;

        /// <summary>Raised when the player opens the battle pass.</summary>
        public event Action PassRequested;

        /// <summary>Raised when the player opens the VIP subscription screen.</summary>
        public event Action VipRequested;

        /// <summary>Raised when the player toggles sound, carrying the new muted state. The composition root applies and persists it.</summary>
        public event Action<bool> MuteChanged;

        private Text _muteLabel;
        private bool _muted;

        // --- Palette (kept in one place so the whole menu stays coherent) ------------------------
        private static readonly Color Ink = new(0.90f, 0.96f, 1f);
        private static readonly Color Backdrop = new(0.02f, 0.02f, 0.07f, 1f);
        private static readonly Color PanelFill = new(0.09f, 0.10f, 0.18f, 0.98f);
        private static readonly Color ChipFill = new(0.13f, 0.15f, 0.24f, 0.98f);
        private static readonly Color Shadow = new(0f, 0f, 0f, 0.38f);
        private static readonly Color PlayCyan = new(0.10f, 0.82f, 0.72f);
        private static readonly Color ShopViolet = new(0.55f, 0.35f, 0.85f);
        private static readonly Color PassTeal = new(0.16f, 0.62f, 0.48f);
        private static readonly Color VipGold = new(0.80f, 0.58f, 0.16f);
        private static readonly Color ClaimGold = new(0.92f, 0.66f, 0.12f);

        public MainMenuScreen(
            Wallet wallet,
            PlayerProfile profile,
            MissionService missions,
            DailyRewardService daily,
            IEventBus bus,
            Transform uiRoot,
            StageService stages = null)
        {
            _wallet = wallet;
            _profile = profile;
            _missions = missions;
            _daily = daily;
            _stages = stages;

            Build(uiRoot);

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
            RefreshStages();
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
            canvas.sortingOrder = 5; // above the HUD (0), below the store (10)

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0f;

            _root = canvasGo;
            _round = MakeRoundedSprite();

            // Full-screen backdrop that IS the play button. Panels and buttons stack on top and
            // swallow their own clicks; a tap anywhere else starts the run — the dominant affordance.
            var backdrop = new GameObject("TapToRun", typeof(RectTransform), typeof(Image), typeof(Button));
            backdrop.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            Stretch(backdrop.GetComponent<RectTransform>());
            backdrop.GetComponent<Image>().color = Backdrop;
            backdrop.GetComponent<Button>().onClick.AddListener(() => StartRequested?.Invoke());

            BuildHeader(canvasGo.transform);
            BuildStagePanel(canvasGo.transform);
            BuildMissionsPanel(canvasGo.transform);
            BuildDailyPanel(canvasGo.transform);
            BuildPlayButton(canvasGo.transform);
            BuildFooter(canvasGo.transform);
            BuildMuteToggle(canvasGo.transform);
        }

        private void BuildHeader(Transform parent)
        {
            // Currency chips: rounded pills with a coloured dot, top corners.
            _coins = Chip(parent, "CoinsChip", new Vector2(0f, 1f), new Vector2(40f, -44f), NeonMaterials.Coin);
            _gems = Chip(parent, "GemsChip", new Vector2(1f, 1f), new Vector2(-40f, -44f), new Color(0.55f, 0.45f, 1f));

            // Title with a magenta drop-shadow behind the cyan, for a lit-sign feel — a plain flat
            // label reads as placeholder text; the offset second copy gives it depth for two labels.
            var shadow = Label(parent, "TitleShadow", new Vector2(0.5f, 1f), new Vector2(6f, -286f), TextAnchor.UpperCenter, 108);
            shadow.text = "NEON RUSH";
            shadow.color = new Color(1f, 0.18f, 0.55f, 0.75f);
            shadow.fontStyle = FontStyle.Bold;

            var title = Label(parent, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -280f), TextAnchor.UpperCenter, 108);
            title.text = "NEON RUSH";
            title.color = NeonMaterials.Player;
            title.fontStyle = FontStyle.Bold;

            // A thin rounded accent bar under the title anchors the wordmark.
            var accent = RoundedImage(parent, "Accent", NeonMaterials.Player);
            Place(accent.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -410f), new Vector2(440f, 10f));

            _bestScore = Label(parent, "Best", new Vector2(0.5f, 1f), new Vector2(0f, -470f), TextAnchor.UpperCenter, 40);
            _bestScore.color = new Color(0.7f, 0.8f, 0.95f);
        }

        /// <summary>A rounded currency pill: a coloured dot plus the balance. Returns the balance label.</summary>
        private Text Chip(Transform parent, string name, Vector2 anchor, Vector2 pos, Color dotColour)
        {
            var chip = RoundedImage(parent, name, ChipFill);
            Place(chip.rectTransform, anchor, pos, new Vector2(300f, 78f));

            var dot = RoundedImage(chip.transform, "Dot", dotColour);
            Place(dot.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(34f, 34f));

            var label = Label(chip.transform, "Value", new Vector2(0f, 0.5f), new Vector2(72f, 0f), TextAnchor.MiddleLeft, 36);
            label.fontStyle = FontStyle.Bold;
            label.rectTransform.sizeDelta = new Vector2(210f, 60f);
            return label;
        }

        /// <summary>
        /// The stage-campaign panel: the current stage's name, its objectives with live progress, and
        /// the reward for clearing it. Built empty and filled by <see cref="RefreshStages"/>; hidden
        /// entirely when no stage service is wired.
        /// </summary>
        private void BuildStagePanel(Transform parent)
        {
            var panel = RoundedImage(parent, "Stage", PanelFill);
            Place(panel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -560f), new Vector2(940f, 300f));
            panel.raycastTarget = true;
            _stagePanel = panel.gameObject;

            var strip = RoundedImage(panel.transform, "StageHeaderStrip", new Color(0.55f, 0.45f, 1f));
            Place(strip.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(912f, 64f));

            _stageTitle = Label(strip.transform, "StageTitle", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 34);
            _stageTitle.color = new Color(0.06f, 0.06f, 0.10f);
            _stageTitle.fontStyle = FontStyle.Bold;
            Stretch(_stageTitle.rectTransform);

            for (var i = 0; i < 3; i++)
            {
                var row = Label(panel.transform, $"StageObj{i}", new Vector2(0f, 1f), new Vector2(40f, -96f - i * 56f), TextAnchor.UpperLeft, 28);
                row.rectTransform.sizeDelta = new Vector2(860f, 50f);
                _stageRows.Add(row);
            }

            _stageReward = Label(panel.transform, "StageReward", new Vector2(0.5f, 0f), new Vector2(0f, 20f), TextAnchor.LowerCenter, 28);
            _stageReward.color = NeonMaterials.Coin;
        }

        private void BuildMissionsPanel(Transform parent)
        {
            // Top-anchored, stacked below the stage panel with a fixed gap, so the two never overlap
            // regardless of screen height (a centre anchor drifted into the stage panel on tall phones).
            var panel = Panel(parent, "Missions", new Vector2(0.5f, 1f), new Vector2(0f, -880f), new Vector2(940f, 360f),
                "TODAY'S MISSIONS", NeonMaterials.Coin);

            for (var i = 0; i < MissionService.ActiveCount; i++)
            {
                var row = Label(panel.transform, $"Mission{i}", new Vector2(0f, 1f), new Vector2(48f, -128f - i * 78f), TextAnchor.UpperLeft, 32);
                row.rectTransform.sizeDelta = new Vector2(850f, 68f);
                _missionRows.Add(row);
            }
        }

        private void BuildDailyPanel(Transform parent)
        {
            var panel = Panel(parent, "Daily", new Vector2(0.5f, 1f), new Vector2(0f, -1280f), new Vector2(940f, 210f),
                null, default);

            _streak = Label(panel.transform, "Streak", new Vector2(0.5f, 1f), new Vector2(0f, -26f), TextAnchor.UpperCenter, 34);
            _streak.fontStyle = FontStyle.Bold;

            _claimButton = RoundedButton(panel.transform, "Claim", new Vector2(0.5f, 0f), new Vector2(0f, 30f),
                new Vector2(540f, 100f), ClaimGold, string.Empty, 36, OnClaimDaily);
            _claimLabel = _claimButton.GetComponentInChildren<Text>();
        }

        private void BuildPlayButton(Transform parent)
        {
            // The focal point. Big, bright, rounded, with the play glyph. It fires the same event as
            // the tap-anywhere backdrop — it is the *sign* pointing at the affordance, not a gate.
            var play = RoundedButton(parent, "Play", new Vector2(0.5f, 0f), new Vector2(0f, 420f),
                new Vector2(720f, 156f), PlayCyan, "▶  PLAY", 60, () => StartRequested?.Invoke());

            var label = play.GetComponentInChildren<Text>();
            label.fontStyle = FontStyle.Bold;
            label.color = new Color(0.02f, 0.06f, 0.08f); // dark ink on bright cyan reads best
        }

        private void BuildFooter(Transform parent)
        {
            // Three glanceable entries across the bottom: SHOP (spend), PASS (season rewards), VIP.
            RoundedButton(parent, "Shop", new Vector2(0.5f, 0f), new Vector2(-330f, 90f),
                new Vector2(300f, 124f), ShopViolet, "SHOP", 36, () => ShopRequested?.Invoke());

            RoundedButton(parent, "Pass", new Vector2(0.5f, 0f), new Vector2(0f, 90f),
                new Vector2(300f, 124f), PassTeal, "PASS", 36, () => PassRequested?.Invoke());

            RoundedButton(parent, "Vip", new Vector2(0.5f, 0f), new Vector2(330f, 90f),
                new Vector2(300f, 124f), VipGold, "★ VIP", 36, () => VipRequested?.Invoke());

            var hint = Label(parent, "Hint", new Vector2(0.5f, 0f), new Vector2(0f, 260f), TextAnchor.LowerCenter, 28);
            hint.text = "tap anywhere to play";
            hint.color = new Color(0.5f, 0.6f, 0.78f);
        }

        /// <summary>
        /// A small sound on/off toggle in the top corner. It swallows its own tap, so flipping sound
        /// never also starts a run. The menu owns the visual state; the composition root applies and
        /// persists it via <see cref="MuteChanged"/>.
        /// </summary>
        private void BuildMuteToggle(Transform parent)
        {
            var go = RoundedButton(parent, "MuteToggle", new Vector2(0f, 1f), new Vector2(40f, -150f),
                new Vector2(220f, 66f), ChipFill, string.Empty, 28, OnMuteTapped);

            _muteLabel = go.GetComponentInChildren<Text>();
            _muteLabel.color = Ink;
            UpdateMuteLabel();
        }

        /// <summary>Sets the initial toggle state (restored from the save) without raising the change event.</summary>
        public void SetMuteState(bool muted)
        {
            _muted = muted;
            UpdateMuteLabel();
        }

        private void OnMuteTapped()
        {
            _muted = !_muted;
            UpdateMuteLabel();
            MuteChanged?.Invoke(_muted);
        }

        private void UpdateMuteLabel()
        {
            if (_muteLabel == null) return;
            _muteLabel.text = _muted ? "SOUND  OFF" : "SOUND  ON";
        }

        // -------------------------------------------------------------------------------
        // Behaviour
        // -------------------------------------------------------------------------------

        private void OnClaimDaily()
        {
            if (_daily.TryClaim(out var claimed) != ClaimRefusal.None) return;

            _claimLabel.text = $"+{claimed.CoinsGranted} COINS" +
                               (claimed.GemsGranted > 0 ? $"  +{claimed.GemsGranted} GEMS" : string.Empty);

            RefreshDaily();
        }

        private void RefreshBalances()
        {
            if (_coins == null) return;

            _coins.text = $"{_wallet.Balance(CurrencyType.Coins):N0}";
            _gems.text = $"{_wallet.Balance(CurrencyType.Gems):N0}";
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

        private void RefreshStages()
        {
            if (_stagePanel == null) return;

            if (_stages == null)
            {
                _stagePanel.SetActive(false);
                return;
            }

            _stagePanel.SetActive(true);

            if (_stages.IsAllComplete)
            {
                _stageTitle.text = "CAMPAIGN COMPLETE";
                _stageRows[0].text = "Every stage cleared — legend status.";
                _stageRows[0].color = new Color(0.4f, 0.9f, 0.6f);
                for (var i = 1; i < _stageRows.Count; i++) _stageRows[i].text = string.Empty;
                _stageReward.text = string.Empty;
                return;
            }

            var stage = _stages.CurrentStage;
            _stageTitle.text = $"STAGE {stage.Number} · {stage.Name}";

            for (var i = 0; i < _stageRows.Count; i++)
            {
                if (i >= stage.Objectives.Count)
                {
                    _stageRows[i].text = string.Empty;
                    continue;
                }

                var objective = stage.Objectives[i];
                var progress = _stages.ProgressAt(i);
                var done = progress >= objective.Target;

                _stageRows[i].text = done
                    ? $"✓ {objective.Description}"
                    : $"{objective.Description}  —  {progress:N0}/{objective.Target:N0}";

                _stageRows[i].color = done ? new Color(0.4f, 0.9f, 0.6f) : new Color(0.85f, 0.9f, 1f);
            }

            _stageReward.text = $"REWARD  +{stage.RewardCoins:N0} COINS" +
                                (stage.RewardGems > 0 ? $"   +{stage.RewardGems} GEMS" : string.Empty);
        }

        private void RefreshDaily()
        {
            var availability = _daily.Availability();

            if (availability == ClaimRefusal.None)
            {
                var (coins, gems) = _daily.NextReward;

                _streak.text = $"DAILY REWARD — DAY {_daily.NextStreakDay}";
                _streak.color = ClaimGold;
                _claimLabel.text = $"CLAIM  +{coins:N0} COINS" + (gems > 0 ? $"  +{gems} GEMS" : string.Empty);
                _claimButton.SetActive(true);

                return;
            }

            _claimButton.SetActive(false);

            _streak.color = new Color(0.6f, 0.7f, 0.85f);
            _streak.text = availability == ClaimRefusal.AlreadyClaimedToday
                ? $"STREAK: {_daily.StreakDays} {(_daily.StreakDays == 1 ? "DAY" : "DAYS")} — come back tomorrow"
                : "daily reward unavailable";
        }

        // -------------------------------------------------------------------------------
        // Widgets
        // -------------------------------------------------------------------------------

        /// <summary>A rounded panel with an optional coloured header strip and title.</summary>
        private GameObject Panel(Transform parent, string name, Vector2 anchor, Vector2 offset, Vector2 size, string header, Color headerColour)
        {
            var panel = RoundedImage(parent, name, PanelFill);
            Place(panel.rectTransform, anchor, offset, size);
            panel.raycastTarget = true; // swallow taps so glancing does not launch a run

            if (header != null)
            {
                var strip = RoundedImage(panel.transform, "HeaderStrip", headerColour);
                Place(strip.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(size.x - 28f, 64f));

                var title = Label(strip.transform, "HeaderText", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 34);
                title.text = header;
                title.color = new Color(0.06f, 0.06f, 0.10f);
                title.fontStyle = FontStyle.Bold;
                Stretch(title.rectTransform);
            }

            return panel.gameObject;
        }

        /// <summary>
        /// A rounded button with a drop-shadow plate behind it, wrapped in a container. Returns the
        /// container so SetActive() hides the shadow with the button — the shadow is a child, not a
        /// loose sibling that would linger as an empty plate when the button is hidden. Its label is a
        /// descendant Text, still reachable with GetComponentInChildren&lt;Text&gt;().
        /// </summary>
        private GameObject RoundedButton(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color colour, string caption, int fontSize, Action onClick)
        {
            var container = new GameObject(name, typeof(RectTransform));
            container.transform.SetParent(parent, worldPositionStays: false);
            Place(container.GetComponent<RectTransform>(), anchor, pos, size);

            // Shadow first (renders behind), filling the container but nudged down for a grounded feel.
            var shadow = RoundedImage(container.transform, "Shadow", Shadow);
            var sr = shadow.rectTransform;
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(0f, -9f);
            sr.offsetMax = new Vector2(0f, -9f);

            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(container.transform, worldPositionStays: false);
            Stretch(go.GetComponent<RectTransform>());

            var image = go.GetComponent<Image>();
            image.sprite = _round;
            image.type = Image.Type.Sliced;
            image.color = colour;

            go.GetComponent<Button>().onClick.AddListener(() => onClick());

            var label = Label(go.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, fontSize);
            label.text = caption;
            label.color = Color.white;
            label.fontStyle = FontStyle.Bold;
            Stretch(label.rectTransform);

            return container;
        }

        /// <summary>An Image using the shared rounded 9-slice sprite.</summary>
        private Image RoundedImage(Transform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);

            var image = go.GetComponent<Image>();
            image.sprite = _round;
            image.type = Image.Type.Sliced;
            image.color = colour;
            image.raycastTarget = false;

            return image;
        }

        private static void Place(RectTransform rect, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
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
            text.color = Ink;
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

        /// <summary>
        /// Builds a small white rounded-rectangle 9-slice sprite once, at runtime. Tinted per Image,
        /// it gives every panel and button rounded corners with no imported texture and no custom
        /// shader — so, like the rest of the game's visuals, there is nothing here to strip on device.
        /// </summary>
        private static Sprite MakeRoundedSprite(int size = 48, int radius = 16)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color32[size * size];
            var half = size / 2f;
            var inner = half - radius;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Signed distance to a rounded rectangle; alpha fades over the last pixel for AA.
                    var dx = Mathf.Abs(x + 0.5f - half) - inner;
                    var dy = Mathf.Abs(y + 0.5f - half) - inner;
                    var outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                    var inside = Mathf.Min(Mathf.Max(dx, dy), 0f);
                    var dist = outside + inside - radius;

                    var alpha = Mathf.Clamp01(0.5f - dist);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();

            var sprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));

            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
