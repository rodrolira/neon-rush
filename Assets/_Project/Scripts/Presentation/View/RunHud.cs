using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The in-run HUD: score, coins, distance, and the game-over panel.
    ///
    /// Built entirely in code against Unity's built-in font, with no prefabs, no scene references
    /// and no TextMeshPro asset import. That is a deliberate constraint for this stage: a HUD
    /// authored as a prefab is a binary file that cannot be reviewed in a diff and breaks silently
    /// when a GUID changes. This one is reviewable, versionable, and cannot lose its references.
    ///
    /// It is also, honestly, the part of the project most obviously destined to be replaced — the
    /// real HUD will be a designed UI system. What it must do today is show the numbers that let us
    /// tune the run, and it does that.
    ///
    /// Text is only rewritten when the value it displays actually changes. Assigning to
    /// <c>Text.text</c> allocates a string and dirties the Canvas, which forces a UI rebuild; doing
    /// that every frame for three labels is a measurable and completely avoidable cost on mobile.
    /// </summary>
    public sealed class RunHud : IDisposable
    {
        private readonly RunSession _session;
        private readonly List<IDisposable> _subscriptions = new();

        private Text _score;
        private Text _coins;
        private Text _distance;
        private GameObject _gameOverPanel;
        private Text _gameOverText;

        // Last rendered values, so we can skip redundant Canvas rebuilds.
        private int _lastScore = -1;
        private int _lastCoins = -1;
        private int _lastDistance = -1;

        public RunHud(RunSession session, IEventBus bus, Transform uiRoot)
        {
            _session = session;

            Build(uiRoot);

            _subscriptions.Add(bus.Subscribe<RunEnded>(OnRunEnded));
            _subscriptions.Add(bus.Subscribe<RunStarted>(OnRunStarted));
        }

        /// <summary>Refreshes the live counters. Called once per frame from the game loop.</summary>
        public void Tick()
        {
            if (!_session.IsRunning) return;

            if (_session.Score != _lastScore)
            {
                _lastScore = _session.Score;
                _score.text = $"SCORE  {_lastScore:N0}";
            }

            if (_session.Coins != _lastCoins)
            {
                _lastCoins = _session.Coins;
                _coins.text = $"COINS  {_lastCoins:N0}";
            }

            var metres = (int)_session.Distance;
            if (metres != _lastDistance)
            {
                _lastDistance = metres;
                _distance.text = $"{metres:N0} m";
            }
        }

        private void OnRunStarted(RunStarted _)
        {
            _gameOverPanel.SetActive(false);

            // Force the next Tick to repaint: -1 can never equal a real value.
            _lastScore = _lastCoins = _lastDistance = -1;
        }

        private void OnRunEnded(RunEnded e)
        {
            _gameOverPanel.SetActive(true);
            _gameOverText.text =
                $"RUN OVER\n\n" +
                $"{(int)e.DistanceMetres:N0} m\n" +
                $"{e.CoinsCollected:N0} coins\n" +
                $"score {e.Score:N0}\n\n" +
                $"tap to run again";
        }

        private void Build(Transform uiRoot)
        {
            var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;

            // Scale against a 1080x1920 portrait reference, matching on width. Matching on width
            // rather than height is what keeps the HUD the same physical size across the very wide
            // range of aspect ratios Android ships (18:9, 19.5:9, 20:9, foldables).
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0f;

            _score = Label(canvasGo.transform, "Score", new Vector2(0f, 1f), new Vector2(40f, -40f), TextAnchor.UpperLeft, 44);
            _coins = Label(canvasGo.transform, "Coins", new Vector2(0f, 1f), new Vector2(40f, -100f), TextAnchor.UpperLeft, 44);
            _distance = Label(canvasGo.transform, "Distance", new Vector2(1f, 1f), new Vector2(-40f, -40f), TextAnchor.UpperRight, 44);

            BuildGameOverPanel(canvasGo.transform);
        }

        private void BuildGameOverPanel(Transform parent)
        {
            _gameOverPanel = new GameObject("GameOver", typeof(RectTransform), typeof(Image));
            _gameOverPanel.transform.SetParent(parent, worldPositionStays: false);

            var rect = _gameOverPanel.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = _gameOverPanel.GetComponent<Image>();
            image.color = new Color(0.02f, 0.02f, 0.06f, 0.82f);

            // The panel must not eat the tap that restarts the run.
            image.raycastTarget = false;

            _gameOverText = Label(_gameOverPanel.transform, "GameOverText", new Vector2(0.5f, 0.5f), Vector2.zero, TextAnchor.MiddleCenter, 56);
            var textRect = _gameOverText.GetComponent<RectTransform>();
            textRect.sizeDelta = new Vector2(900f, 900f);

            _gameOverPanel.SetActive(false);
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
            rect.sizeDelta = new Vector2(700f, 70f);

            var text = go.GetComponent<Text>();

            // Unity 6 renamed the built-in font from Arial.ttf to LegacyRuntime.ttf. Using the
            // built-in font is what lets this HUD exist with zero imported assets.
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = alignment;
            text.color = new Color(0.85f, 1f, 0.98f);
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = string.Empty;

            return text;
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }
    }
}
