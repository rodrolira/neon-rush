using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Core.Events;
using UnityEngine;
using UnityEngine.UI;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// The screen-space "juice" layer: the impact flash and the speed lines.
    ///
    /// Both are built from plain uGUI <see cref="Image"/>s with no sprite, on purpose. An Image with
    /// no sprite draws a solid quad through the built-in UI/Default shader, which is always compiled
    /// into the build — so this whole effect layer is immune to the runtime shader-stripping that a
    /// code-built ParticleSystem would trip over on device (a black-screen trap we have already been
    /// bitten by once). Nothing here allocates per frame, and nothing here touches gameplay: it
    /// listens to the same event bus everything else does and only ever draws.
    ///
    /// Everything animates on the UNSCALED clock, so the death flash keeps fading smoothly through
    /// the hit-stop (a brief Time.timeScale freeze) that fires on the same frame — see GameBootstrap.
    /// </summary>
    public sealed class RunJuice : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = new();

        // --- Impact flash ------------------------------------------------------------------

        private Image _flash;
        private float _flashAlpha;

        /// <summary>Peak whiteness of the death flash. High enough to punctuate, low enough not to blind.</summary>
        private const float DeathFlashStrength = 0.5f;

        /// <summary>How fast the flash fades back to nothing, in alpha per second.</summary>
        private const float FlashDecay = 3.2f;

        // --- Speed lines -------------------------------------------------------------------

        private CanvasGroup _speedLines;
        private readonly List<RectTransform> _streaks = new();
        private float _speedLineAlpha;   // smoothed, so it breathes in and out rather than snapping
        private float _surge;            // transient milestone boost, 0..1, decays to 0
        private float _time;

        /// <summary>Streaks per screen edge. Kept low: speed lines read as speed when sparse, as noise when dense.</summary>
        private const int StreaksPerSide = 6;

        /// <summary>Group alpha of the speed lines at the speed cap. Deliberately restrained.</summary>
        private const float SpeedLineMaxAlpha = 0.32f;

        /// <summary>How quickly the speed-line intensity follows the actual speed. Slow = it breathes.</summary>
        private const float SpeedLineSharpness = 5f;

        public RunJuice(Transform uiRoot, IEventBus bus)
        {
            Build(uiRoot);

            // The flash is the crash's exclamation mark; only a real impact earns it. A deliberate
            // quit is not an impact, exactly as the camera shake is gated the same way.
            _subscriptions.Add(bus.Subscribe<RunEnded>(e =>
            {
                if (e.Cause == DeathCause.HitObstacle) _flashAlpha = DeathFlashStrength;
            }));
        }

        /// <summary>
        /// A speed surge — one gear-change worth of extra intensity. Called on each distance
        /// milestone so the player FEELS the acceleration land, instead of only inferring it from
        /// dying more often. Pairs with the camera's FOV punch fired from the same event.
        /// </summary>
        public void Surge() => _surge = 1f;

        /// <param name="unscaledDeltaTime">Real time, so the flash keeps fading through a hit-stop.</param>
        /// <param name="normalisedSpeed">0 at base speed, 1 at the cap.</param>
        /// <param name="playing">Speed lines only show during a live run, never over a menu or death screen.</param>
        public void Tick(float unscaledDeltaTime, float normalisedSpeed, bool playing)
        {
            _time += unscaledDeltaTime;

            TickFlash(unscaledDeltaTime);
            TickSpeedLines(unscaledDeltaTime, normalisedSpeed, playing);
        }

        private void TickFlash(float dt)
        {
            if (_flashAlpha <= 0f)
            {
                if (_flash.enabled) _flash.enabled = false;
                return;
            }

            _flashAlpha = Mathf.Max(0f, _flashAlpha - FlashDecay * dt);

            _flash.enabled = true;
            var colour = _flash.color;
            colour.a = _flashAlpha;
            _flash.color = colour;
        }

        private void TickSpeedLines(float dt, float normalisedSpeed, bool playing)
        {
            _surge = Mathf.Max(0f, _surge - dt * 1.6f);

            // Ease the speed contribution so lines stay invisible at a jog and only bloom in near
            // the top end, where the sense of speed actually needs help.
            var eased = normalisedSpeed * normalisedSpeed;
            var target = playing ? Mathf.Clamp01(eased + _surge * 0.6f) * SpeedLineMaxAlpha : 0f;

            var t = 1f - Mathf.Exp(-SpeedLineSharpness * dt);
            _speedLineAlpha = Mathf.Lerp(_speedLineAlpha, target, t);
            _speedLines.alpha = _speedLineAlpha;

            if (_speedLineAlpha <= 0.001f) return;

            // A cheap shimmer so the lines feel like rushing air rather than static decals: each
            // streak slides along its own axis on an index-offset sine, wrapping within its slot.
            for (var i = 0; i < _streaks.Count; i++)
            {
                var streak = _streaks[i];
                var phase = _time * 6f + i * 1.3f;
                var slide = Mathf.Repeat(phase, 2f) - 1f;           // -1..1 saw-ish
                var pos = streak.anchoredPosition;
                pos.y = _streakBaseY[i] + slide * 120f;
                streak.anchoredPosition = pos;
            }
        }

        private readonly List<float> _streakBaseY = new();

        private void Build(Transform uiRoot)
        {
            var canvasGo = new GameObject("RunJuice", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(uiRoot, worldPositionStays: false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the HUD so the flash covers everything; the speed lines live at the edges and
            // do not meaningfully overlap the HUD's corner readouts.
            canvas.sortingOrder = 50;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;

            BuildSpeedLines(canvasGo.transform);
            BuildFlash(canvasGo.transform);
        }

        private void BuildSpeedLines(Transform parent)
        {
            var container = new GameObject("SpeedLines", typeof(RectTransform), typeof(CanvasGroup));
            container.transform.SetParent(parent, worldPositionStays: false);

            var rect = container.GetComponent<RectTransform>();
            Stretch(rect);

            _speedLines = container.GetComponent<CanvasGroup>();
            _speedLines.alpha = 0f;
            _speedLines.interactable = false;
            _speedLines.blocksRaycasts = false;

            for (var side = 0; side < 2; side++)
            {
                var left = side == 0;

                for (var i = 0; i < StreaksPerSide; i++)
                {
                    // Spread the streaks vertically across the middle band of the screen and taper
                    // their length so the set does not read as a picket fence.
                    var fraction = (i + 0.5f) / StreaksPerSide;         // 0..1 down the band
                    var baseY = Mathf.Lerp(-520f, 520f, fraction);
                    var length = Mathf.Lerp(150f, 340f, Mathf.Abs(0.5f - fraction) * 2f);

                    var streak = BuildStreak(container.transform, left, baseY, length, i);
                    _streaks.Add(streak);
                    _streakBaseY.Add(baseY);
                }
            }
        }

        private static RectTransform BuildStreak(Transform parent, bool left, float baseY, float length, int index)
        {
            var go = new GameObject(left ? $"StreakL{index}" : $"StreakR{index}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);

            var rect = go.GetComponent<RectTransform>();
            var anchorX = left ? 0f : 1f;
            rect.anchorMin = new Vector2(anchorX, 0.5f);
            rect.anchorMax = new Vector2(anchorX, 0.5f);
            rect.pivot = new Vector2(anchorX, 0.5f);

            // Near the edge, nudged inward a touch, angled so the lines sweep toward the horizon.
            var inset = 24f + (index % 3) * 26f;
            rect.anchoredPosition = new Vector2(left ? inset : -inset, baseY);
            rect.sizeDelta = new Vector2(5f, length);
            rect.localRotation = Quaternion.Euler(0f, 0f, left ? -8f : 8f);

            var image = go.GetComponent<Image>();
            // Cyan-white to sit inside the neon palette. Low base alpha; the CanvasGroup does the
            // speed-driven fade on top of this.
            image.color = new Color(0.75f, 1f, 1f, 0.5f);
            image.raycastTarget = false;

            return rect;
        }

        private void BuildFlash(Transform parent)
        {
            var go = new GameObject("Flash", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);

            Stretch(go.GetComponent<RectTransform>());

            _flash = go.GetComponent<Image>();
            _flash.color = new Color(1f, 0.96f, 0.98f, 0f);
            _flash.raycastTarget = false;   // the flash must never eat the tap that restarts the run
            _flash.enabled = false;
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
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }
    }
}
