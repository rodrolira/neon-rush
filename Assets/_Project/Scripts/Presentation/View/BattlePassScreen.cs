using System;
using System.Collections.Generic;
using NeonRush.Application.BattlePass;
using NeonRush.Core.Events;
using NeonRush.Domain.BattlePass;
using NeonRush.Domain.Economy;
using NeonRush.Presentation.Visuals;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The battle-pass screen, built in code like every other menu here.
    ///
    /// It is grey-box — the real thing gets art, a scrolling reward reel and a season timer — but
    /// every interaction is live against the same <see cref="BattlePassService"/> the shipping game
    /// uses: XP earned by playing moves the bar, claiming a tier actually credits the wallet or grants
    /// the entitlement, and buying premium actually debits gems and unlocks the premium track. Drawn
    /// entirely from uGUI graphics, so it ships no shader of its own and cannot be stripped on device.
    /// </summary>
    public sealed class BattlePassScreen : IDisposable
    {
        private readonly BattlePassService _pass;
        private readonly Wallet _wallet;
        private readonly List<IDisposable> _subscriptions = new();

        private GameObject _root;
        private Text _title;
        private Text _progressLabel;
        private Text _gemBalance;
        private RectTransform _barFill;
        private Button _premiumButton;
        private Text _premiumLabel;
        private Button _claimAllButton;
        private Text _toast;

        private readonly List<TierRow> _rows = new();

        private const float RowHeight = 150f;
        private const float RowGap = 10f;

        /// <summary>The widgets in one tier row that change as the player earns and claims.</summary>
        private sealed class TierRow
        {
            public int Level;
            public Button Free;
            public Text FreeLabel;
            public Button Premium;
            public Text PremiumLabel;
        }

        public BattlePassScreen(BattlePassService pass, Wallet wallet, IEventBus bus, Transform uiRoot)
        {
            _pass = pass ?? throw new ArgumentNullException(nameof(pass));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));

            if (bus == null) throw new ArgumentNullException(nameof(bus));

            Build(uiRoot);

            // Everything that can change the screen while it is open, refreshes it: earning XP,
            // claiming a reward, buying premium, or any balance move.
            _subscriptions.Add(bus.Subscribe<BattlePassProgressed>(_ => RefreshIfOpen()));
            _subscriptions.Add(bus.Subscribe<BattlePassRewardClaimed>(_ => RefreshIfOpen()));
            _subscriptions.Add(bus.Subscribe<BattlePassPremiumUnlocked>(_ => RefreshIfOpen()));
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
            var canvasGo = new GameObject("BattlePass", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 12; // above the HUD and level with the store

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

        private static void BuildDimmer(Transform parent)
        {
            var go = new GameObject("Dimmer", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            Stretch(go.GetComponent<RectTransform>());

            var image = go.GetComponent<Image>();
            image.color = new Color(0.01f, 0.01f, 0.05f, 0.95f);
            image.raycastTarget = true; // swallow taps meant for the game behind it
        }

        private void BuildHeader(Transform parent)
        {
            _title = Label(parent, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -50f), TextAnchor.UpperCenter, 58);
            _title.color = NeonMaterials.Coin;

            _gemBalance = Label(parent, "Gems", new Vector2(1f, 1f), new Vector2(-40f, -140f), TextAnchor.UpperRight, 36);

            _progressLabel = Label(parent, "Progress", new Vector2(0f, 1f), new Vector2(40f, -140f), TextAnchor.UpperLeft, 34);

            // The XP bar: a dark track with a bright fill anchored to its left edge, whose width we
            // scale to overall pass progress in Refresh().
            var track = new GameObject("BarTrack", typeof(RectTransform), typeof(Image));
            track.transform.SetParent(parent, worldPositionStays: false);
            var trackRect = track.GetComponent<RectTransform>();
            trackRect.anchorMin = new Vector2(0.5f, 1f);
            trackRect.anchorMax = new Vector2(0.5f, 1f);
            trackRect.pivot = new Vector2(0.5f, 1f);
            trackRect.sizeDelta = new Vector2(960f, 26f);
            trackRect.anchoredPosition = new Vector2(0f, -200f);
            track.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.2f);

            var fill = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(track.transform, worldPositionStays: false);
            _barFill = fill.GetComponent<RectTransform>();
            _barFill.anchorMin = new Vector2(0f, 0f);
            _barFill.anchorMax = new Vector2(0f, 1f);   // left-anchored; width set in Refresh
            _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.offsetMin = Vector2.zero;
            _barFill.offsetMax = Vector2.zero;
            _barFill.sizeDelta = new Vector2(0f, 0f);
            fill.GetComponent<Image>().color = NeonMaterials.Player;
            fill.GetComponent<Image>().raycastTarget = false;

            // Premium + claim-all actions.
            (_premiumButton, _premiumLabel) = BuildButton(parent, "PREMIUM", OnBuyPremium, new Color(0.55f, 0.35f, 0.85f));
            var pRect = _premiumButton.GetComponent<RectTransform>();
            pRect.anchorMin = new Vector2(0f, 1f);
            pRect.anchorMax = new Vector2(0f, 1f);
            pRect.pivot = new Vector2(0f, 1f);
            pRect.sizeDelta = new Vector2(460f, 96f);
            pRect.anchoredPosition = new Vector2(40f, -246f);

            var (claimAllButton, claimAllLabel) = BuildButton(parent, "CLAIM ALL", OnClaimAll, new Color(0.16f, 0.6f, 0.45f));
            _claimAllButton = claimAllButton;
            claimAllLabel.text = "CLAIM ALL";
            var cRect = _claimAllButton.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(1f, 1f);
            cRect.anchorMax = new Vector2(1f, 1f);
            cRect.pivot = new Vector2(1f, 1f);
            cRect.sizeDelta = new Vector2(400f, 96f);
            cRect.anchoredPosition = new Vector2(-40f, -246f);
        }

        private void BuildScrollList(Transform parent)
        {
            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(parent, worldPositionStays: false);

            var viewport = viewportGo.GetComponent<RectTransform>();
            viewport.anchorMin = new Vector2(0.5f, 0.5f);
            viewport.anchorMax = new Vector2(0.5f, 0.5f);
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.sizeDelta = new Vector2(1000f, 1120f);
            viewport.anchoredPosition = new Vector2(0f, -120f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewport, worldPositionStays: false);

            var content = contentGo.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            var track = _pass.State.Track;
            var totalHeight = track.TierCount * (RowHeight + RowGap) + RowGap;
            content.sizeDelta = new Vector2(0f, totalHeight);

            for (var i = 0; i < track.TierCount; i++)
            {
                _rows.Add(BuildRow(content, track.TierAt(i + 1), i));
            }

            var scroll = viewportGo.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 34f;
        }

        private TierRow BuildRow(Transform content, BattlePassTier tier, int index)
        {
            var level = tier.Level;

            var rowGo = new GameObject($"Tier_{level}", typeof(RectTransform), typeof(Image));
            rowGo.transform.SetParent(content, worldPositionStays: false);

            var rect = rowGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(20f, 0f);
            rect.offsetMax = new Vector2(-20f, 0f);
            rect.sizeDelta = new Vector2(0f, RowHeight);
            rect.anchoredPosition = new Vector2(0f, -(RowGap + index * (RowHeight + RowGap)));

            rowGo.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.16f, 0.95f);

            var levelLabel = Label(rowGo.transform, "Level", new Vector2(0f, 0.5f), new Vector2(28f, 0f), TextAnchor.MiddleLeft, 40);
            levelLabel.text = $"TIER\n{level}";
            levelLabel.color = new Color(0.7f, 0.8f, 0.95f);

            // Free reward: description on top, claim button under it.
            var freeDesc = Label(rowGo.transform, "FreeDesc", new Vector2(0.5f, 1f), new Vector2(-30f, -14f), TextAnchor.UpperCenter, 28);
            freeDesc.text = Describe(tier.Free);
            freeDesc.color = new Color(0.85f, 0.95f, 1f);

            var (freeBtn, freeLabel) = BuildButton(rowGo.transform, "", () => OnClaimFree(level), new Color(0.16f, 0.6f, 0.45f));
            PlaceRowButton(freeBtn, xAnchor: 0.5f, xOffset: -30f);

            // Premium reward: same, on the right.
            var premDesc = Label(rowGo.transform, "PremDesc", new Vector2(1f, 1f), new Vector2(-30f, -14f), TextAnchor.UpperRight, 28);
            premDesc.text = Describe(tier.Premium);
            premDesc.color = new Color(0.85f, 0.75f, 1f);

            var (premBtn, premLabel) = BuildButton(rowGo.transform, "", () => OnClaimPremium(level), new Color(0.55f, 0.35f, 0.85f));
            PlaceRowButton(premBtn, xAnchor: 1f, xOffset: -30f);

            return new TierRow
            {
                Level = level,
                Free = freeBtn,
                FreeLabel = freeLabel,
                Premium = premBtn,
                PremiumLabel = premLabel,
            };
        }

        private static void PlaceRowButton(Button button, float xAnchor, float xOffset)
        {
            var rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(xAnchor, 0f);
            rect.anchorMax = new Vector2(xAnchor, 0f);
            rect.pivot = new Vector2(xAnchor, 0f);
            rect.sizeDelta = new Vector2(300f, 64f);
            rect.anchoredPosition = new Vector2(xOffset, 16f);
        }

        private void BuildToast(Transform parent)
        {
            _toast = Label(parent, "Toast", new Vector2(0.5f, 0f), new Vector2(0f, 200f), TextAnchor.LowerCenter, 34);
            _toast.color = NeonMaterials.Player;
        }

        private void BuildCloseButton(Transform parent)
        {
            var (button, label) = BuildButton(parent, "CLOSE", Hide, new Color(0.16f, 0.55f, 0.85f));
            label.text = "CLOSE";

            var rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(400f, 104f);
            rect.anchoredPosition = new Vector2(0f, 70f);
        }

        // -------------------------------------------------------------------------------
        // Interaction
        // -------------------------------------------------------------------------------

        private void OnClaimFree(int level)
        {
            var reward = _pass.ClaimFree(level);
            if (reward.IsSomething) SetToast($"Claimed {Describe(reward).Replace('\n', ' ')}", NeonMaterials.Player);
            Refresh();
        }

        private void OnClaimPremium(int level)
        {
            var reward = _pass.ClaimPremium(level);
            if (reward.IsSomething) SetToast($"Claimed {Describe(reward).Replace('\n', ' ')}", NeonMaterials.Player);
            Refresh();
        }

        private void OnClaimAll()
        {
            var rewards = _pass.ClaimAll();
            SetToast(rewards.Count > 0 ? $"Claimed {rewards.Count} rewards" : "Nothing to claim",
                rewards.Count > 0 ? NeonMaterials.Player : NeonMaterials.Obstacle);
            Refresh();
        }

        private void OnBuyPremium()
        {
            if (_pass.State.PremiumOwned) return;

            if (_pass.TryUnlockPremium())
            {
                SetToast("Premium unlocked!", NeonMaterials.Player);
            }
            else
            {
                SetToast("Not enough gems", NeonMaterials.Obstacle);
            }

            Refresh();
        }

        // -------------------------------------------------------------------------------
        // Refresh
        // -------------------------------------------------------------------------------

        private void Refresh()
        {
            var state = _pass.State;

            _title.text = $"BATTLE PASS · {state.SeasonId.ToUpperInvariant()}";
            _progressLabel.text = $"TIER {state.CurrentTier} / {state.Track.TierCount}   ({state.Xp:N0}/{state.MaxXp:N0} XP)";
            _gemBalance.text = $"GEMS  {_wallet.Balance(CurrencyType.Gems):N0}";

            var fraction = state.MaxXp > 0 ? (float)state.Xp / state.MaxXp : 0f;
            _barFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);

            if (state.PremiumOwned)
            {
                _premiumLabel.text = "PREMIUM ✓";
                _premiumButton.interactable = false;
            }
            else
            {
                _premiumLabel.text = $"UNLOCK PREMIUM\n{_pass.PremiumGemPrice:N0} gems";
                _premiumButton.interactable = true;
            }

            var anyClaimable = false;

            foreach (var row in _rows)
            {
                anyClaimable |= RefreshTrack(row.Free, row.FreeLabel, state.CanClaimFree(row.Level),
                    state.IsClaimedFree(row.Level), row.Level <= state.CurrentTier, premiumLocked: false);

                var premiumLocked = !state.PremiumOwned;
                anyClaimable |= RefreshTrack(row.Premium, row.PremiumLabel, state.CanClaimPremium(row.Level),
                    state.IsClaimedPremium(row.Level), row.Level <= state.CurrentTier, premiumLocked);
            }

            _claimAllButton.interactable = anyClaimable;
        }

        /// <summary>Sets one track's claim button to the right state. Returns true if it is claimable now.</summary>
        private static bool RefreshTrack(Button button, Text label, bool canClaim, bool claimed, bool unlocked, bool premiumLocked)
        {
            if (claimed)
            {
                button.interactable = false;
                label.text = "CLAIMED";
                return false;
            }

            if (canClaim)
            {
                button.interactable = true;
                label.text = "CLAIM";
                return true;
            }

            button.interactable = false;
            label.text = premiumLocked && unlocked ? "PREMIUM" : unlocked ? "—" : "LOCKED";
            return false;
        }

        private static string Describe(BattlePassReward reward) => reward.Kind switch
        {
            BattlePassRewardKind.Coins => $"+{reward.Amount:N0}\ncoins",
            BattlePassRewardKind.Gems => $"+{reward.Amount:N0}\ngems",
            BattlePassRewardKind.Item => "cosmetic\nskin",
            _ => "—",
        };

        // -------------------------------------------------------------------------------
        // Widgets
        // -------------------------------------------------------------------------------

        private void SetToast(string message, Color? colour = null)
        {
            _toast.text = message;
            if (colour.HasValue) _toast.color = colour.Value;
        }

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

            var text = Label(go.transform, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 30);
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
            rect.sizeDelta = new Vector2(360f, 120f);

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
