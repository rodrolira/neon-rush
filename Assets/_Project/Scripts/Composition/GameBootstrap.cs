using System;
using NeonRush.Application.Ads;
using NeonRush.Application.Analytics;
using NeonRush.Application.Config;
using NeonRush.Application.Economy;
using NeonRush.Application.Events;
using NeonRush.Application.Progression;
using NeonRush.Application.Run;
using NeonRush.Application.Save;
using NeonRush.Application.States;
using NeonRush.Application.Store;
using NeonRush.Core.DI;
using NeonRush.Core.Events;
using NeonRush.Domain.Ads;
using NeonRush.Domain.Economy;
using NeonRush.Domain.Inventory;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Run;
using NeonRush.Domain.Save;
using NeonRush.Domain.Store;
using NeonRush.Infrastructure.Ads;
using NeonRush.Infrastructure.Analytics;
using NeonRush.Infrastructure.Iap;
using NeonRush.Infrastructure.Remote;
using NeonRush.Infrastructure.Save;
using NeonRush.Infrastructure.Time;
using NeonRush.Presentation.Input;
using NeonRush.Presentation.Player;
using NeonRush.Presentation.View;
using NeonRush.Presentation.Visuals;
using NeonRush.Presentation.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace NeonRush.Composition
{
    /// <summary>
    /// The composition root. The one place in the codebase that is allowed to know every concrete
    /// type, and the one place where the object graph is wired.
    ///
    /// It is also the game's single <c>Update</c>. Systems are plain C# objects ticked from here in
    /// an explicit, readable order, rather than each being a MonoBehaviour with its own Update
    /// callback. Two reasons:
    ///
    ///  · <b>Cost.</b> Every MonoBehaviour.Update is a managed-to-native call that Unity makes
    ///    individually. Hundreds of them is a measurable frame cost for no benefit.
    ///  · <b>Order.</b> Unity does not guarantee the order in which Update runs across objects.
    ///    "Did collision run before or after the player moved this frame?" must have a single
    ///    answer, and here it is written down in one place, in the order it executes.
    ///
    /// The whole scene is constructed from code — no prefabs, no scene-authored references. A
    /// missing reference on a prefab is a null at runtime, discovered by a player. A missing
    /// argument here is a compile error, discovered by CI.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Header("Run tuning")]
        [Tooltip("Seed for the procedural track. 0 = random each run.")]
        [SerializeField] private int _seed;

        [Header("Ads (development)")]
        [Tooltip("Simulate ads in the Editor so the revive and double-coins flows can be exercised " +
                 "without an SDK. MUST be off in a release build.")]
        [SerializeField] private bool _simulateAds = true;

        [Tooltip("What a simulated ad reports. Set to Skipped and play for five minutes — if anything " +
                 "is granted, there is a payout bug.")]
        [SerializeField] private AdResult _simulatedAdResult = AdResult.Completed;

        private Container _container;
        private EventBus _bus;
        private GameStateMachine _states;
        private RunSession _session;
        private RunTuning _tuning;
        private Wallet _wallet;
        private RunRewardService _rewards;
        private PlayerProfile _profile;
        private SaveService _save;
        private AdDirector _adDirector;
        private Inventory _inventory;
        private StoreCatalog _catalog;
        private StoreService _store;
        private StoreScreen _storeScreen;
        private LocalRemoteConfig _remote;
        private GameConfigService _config;
        private AnalyticsReporter _analyticsReporter;
        private NeonRush.Application.Missions.MissionService _missions;

        private SwipeInput _input;
        private PlayerMotor _player;
        private TrackStreamer _track;
        private CollisionSystem _collisions;
        private RunCameraRig _camera;
        private RunHud _hud;
        private NeonMaterials _materials;

        private IDisposable _runEndedSubscription;

        /// <summary>Set when a run ends; the next tap starts a new one.</summary>
        private bool _awaitingRestart;

        /// <summary>Guards against the tap that killed the player also restarting the run in the same frame.</summary>
        private float _restartArmedAt;

        private void Awake()
        {
            ConfigureQualityForMobile();

            _bus = new EventBus();
            _states = new GameStateMachine();

            var clock = new SystemClock();

            _container = new Container();
            _container.RegisterInstance<IEventBus>(_bus);
            _container.RegisterInstance<IClock>(clock);
            _container.RegisterInstance(_states);

            // --- Remote config --------------------------------------------------------------
            //
            // Built first, because it decides the numbers everything else is constructed with. Bound
            // to LocalRemoteConfig until Firebase is integrated: it returns the compiled-in defaults,
            // which are a complete, balanced game. The async fetch (kicked off later) then swaps in
            // remote values for the NEXT run — we never block the boot waiting for the network.
            _remote = new LocalRemoteConfig();
            _container.RegisterInstance<IRemoteConfigService>(_remote);

            _config = new GameConfigService(_remote);
            _container.RegisterInstance(_config);

            // --- Analytics ------------------------------------------------------------------
            //
            // RecordingAnalytics until Firebase lands: it logs events to the console in the Editor
            // (a designer can watch the funnel fire while playing) and proves the instrumentation
            // pipeline, so that when the SDK adapter is bound, day-one data flows through a taxonomy
            // that has already been exercised. Swapping in FirebaseAnalytics is one binding.
            IAnalyticsService analytics = new RecordingAnalytics(logToConsole: UnityEngine.Application.isEditor);
            _container.RegisterInstance(analytics);

            _analyticsReporter = new AnalyticsReporter(analytics, _bus);
            _container.RegisterInstance(_analyticsReporter);

            // Every balance number now flows through the config service, which clamps each remote
            // value to a range that is always playable. There is no `new RunTuning()` anywhere in the
            // shipping path — that is what "no hardcoded balance values" means in practice.
            _tuning = _config.BuildRunTuning();
            _container.RegisterInstance(_tuning);

            _session = new RunSession(_tuning, _bus);
            _container.RegisterInstance(_session);

            // --- Persistence ----------------------------------------------------------------
            //
            // Load BEFORE anything that owns player state is constructed, so the wallet and profile
            // are born holding the real values rather than being zeroed and then patched. Patching
            // afterwards would publish a spurious CurrencyChanged on boot, and the HUD would animate
            // the player's balance up from zero every single launch.
            ISaveStore store = FileSaveStore.Default();
            _container.RegisterInstance(store);

            var loaded = store.Load();

            if (!loaded.Restored && loaded.Failure != LoadFailure.NotFound)
            {
                // A real load failure, not a fresh install. The player is about to lose progress, so
                // this must be loud — and in the shipping game it goes to Crashlytics, not just the
                // console, because we need to know how often it happens in the wild.
                Debug.LogError($"[NeonRush] Save could not be restored: {loaded.Failure}. Starting a fresh profile.");
            }

            // The wallet is a local prediction of a server-authoritative balance (ARCHITECTURE.md §8).
            _wallet = new Wallet(_bus, loaded.Data.Coins, loaded.Data.Gems);
            _container.RegisterInstance(_wallet);

            _profile = new PlayerProfile(_bus, loaded.Data);
            _container.RegisterInstance(_profile);

            _inventory = new Inventory(_bus, loaded.Data.OwnedItems);
            _container.RegisterInstance(_inventory);

            _rewards = new RunRewardService(_wallet, _bus);
            _container.RegisterInstance(_rewards);

            _save = new SaveService(store, _wallet, _profile, _inventory, _bus, clock, loaded.Data.AdsRemoved);
            _container.RegisterInstance(_save);

            // --- Daily reward ---------------------------------------------------------------
            //
            // Claimed automatically at boot for now: the grey-box stage has no main menu to hang a
            // claim ritual on, and an unclaimed reward that silently expires would punish players
            // for our missing UI. When the menu exists, this becomes an explicit, celebratory claim.
            var dailyRewards = new Domain.Retention.DailyRewardService(
                _wallet, clock, _bus, loaded.Data.LastDailyClaimUtc, loaded.Data.DailyStreakDays);

            _container.RegisterInstance(dailyRewards);
            _save.DailyRewards = dailyRewards;

            if (dailyRewards.TryClaim(out var claimed) == Domain.Retention.ClaimRefusal.None)
            {
                Debug.Log($"[NeonRush] Daily reward claimed: day {claimed.StreakDay} of streak, " +
                          $"+{claimed.CoinsGranted} coins, +{claimed.GemsGranted} gems.");
                _save.MarkDirty();
            }

            // --- Missions -------------------------------------------------------------------
            //
            // Three per day, deterministic from the UTC date, progress driven entirely by events the
            // game already publishes. Order matters here: RefreshIfNewDay builds today's set FIRST,
            // then RestoreProgress lays saved progress onto it (progress from a previous day is
            // discarded inside RestoreProgress — yesterday's half-done mission does not carry over).
            _missions = new NeonRush.Application.Missions.MissionService(_wallet, clock, _bus);
            _container.RegisterInstance(_missions);

            _missions.RefreshIfNewDay();

            var savedMissions = new System.Collections.Generic.List<(string, int, bool)>();
            foreach (var m in loaded.Data.Missions)
            {
                savedMissions.Add((m.Id, m.Progress, m.Rewarded));
            }

            _missions.RestoreProgress(loaded.Data.MissionDay, savedMissions);
            _save.Missions = _missions;

            foreach (var mission in _missions.Active)
            {
                Debug.Log($"[NeonRush] Daily mission: {mission.Definition.Description} " +
                          $"({mission.Progress}/{mission.Definition.Target})" +
                          (mission.Rewarded ? " — completed" : string.Empty));
            }

            // --- Ads ------------------------------------------------------------------------
            //
            // A release build binds NullAdsService until the AdMob adapter is integrated; the game is
            // complete and shippable either way. SimulatedAdsService exists so the revive and
            // double-coins flows can actually be exercised in the Editor without an SDK, a network
            // and 30 seconds of a real ad per attempt.
            IAdsService ads = _simulateAds && UnityEngine.Application.isEditor
                ? new SimulatedAdsService(_simulatedAdResult)
                : new NullAdsService();

            _container.RegisterInstance(ads);

            var adPolicy = new AdPolicy(_config.BuildAdPolicyConfig(), clock);
            _container.RegisterInstance(adPolicy);

            // A player who bought ad removal must not see an interstitial on the very first run after
            // a reinstall, before any server sync has happened. The entitlement is restored from the
            // local save immediately.
            if (loaded.Data.AdsRemoved)
            {
                ads.DisableInterstitials();
                adPolicy.DisableInterstitials();
            }

            _adDirector = new AdDirector(ads, adPolicy, _rewards, _profile, _bus);
            _container.RegisterInstance(_adDirector);

            // --- Store ----------------------------------------------------------------------
            //
            // The receipt validator is the security boundary of the whole economy. DevReceiptValidator
            // approves everything, and it contains a hard guard that REFUSES to run in a release
            // build — so shipping it by accident fails purchases loudly instead of silently minting
            // currency for anyone who asks. Bind ServerReceiptValidator once the Cloud Function
            // exists; see Infrastructure/Iap/ReceiptValidators.cs.
            IIapService iap = new SimulatedIapService();
            IReceiptValidator validator = new DevReceiptValidator();

            _container.RegisterInstance(iap);
            _container.RegisterInstance(validator);

            _catalog = StoreCatalog.Default();

            // Apply any remotely-overridden currency prices over the defaults. On a fresh offline
            // boot this is a no-op — the defaults already ARE the prices — so the store is always
            // coherent regardless of network state.
            _config.ApplyStorePrices(_catalog);
            _container.RegisterInstance(_catalog);

            _store = new StoreService(_catalog, _wallet, _inventory, iap, validator, ads, _bus);
            _container.RegisterInstance(_store);

            BuildScene();

            // Kick the async fetch AFTER the game is fully built and playable on defaults. When it
            // completes, the new values are live for the next objects that read them (the next run's
            // tuning, the next store open). We never rebuild the live run underneath the player.
            _remote.Fetch(success =>
                Debug.Log($"[NeonRush] Remote config fetch {(success ? "succeeded" : "failed")}; running on "
                          + (success ? "remote values" : "compiled defaults") + "."));

            _runEndedSubscription = _bus.Subscribe<RunEnded>(OnRunEnded);

            _states.TransitionTo(GameState.MainMenu);
            StartRun();
        }

        private void BuildScene()
        {
            _materials = new NeonMaterials();

            var world = new GameObject("World").transform;
            world.SetParent(transform, worldPositionStays: false);

            // --- Player ---------------------------------------------------------------------
            const float playerHeight = 1.6f;

            var playerGo = PrimitiveFactory.Cube(
                "Player",
                new Vector3(0.8f, playerHeight, 0.8f),
                _materials.Get(NeonMaterials.Player));

            playerGo.transform.SetParent(transform, worldPositionStays: false);

            // The cube mesh is centred on its origin, so a child holds the visual offset upward and
            // leaves the parent transform sitting on the ground plane. Everything downstream —
            // lane maths, jump height, the collision AABB — can then treat y=0 as "feet on floor",
            // which removes a half-height fudge factor from four separate places.
            var playerPivot = new GameObject("PlayerPivot").transform;
            playerPivot.SetParent(transform, worldPositionStays: false);
            playerGo.transform.SetParent(playerPivot, worldPositionStays: false);
            playerGo.transform.localPosition = new Vector3(0f, playerHeight * 0.5f, 0f);

            _player = new PlayerMotor(playerPivot, _tuning, _bus, playerHeight);

            // --- Track ----------------------------------------------------------------------
            var seed = _seed != 0 ? _seed : Environment.TickCount;
            _track = new TrackStreamer(_tuning, world, _materials, seed);

            _collisions = new CollisionSystem(_track, _player, _session, _tuning.ChunkLength);

            // --- Camera ---------------------------------------------------------------------
            var cameraGo = new GameObject("MainCamera", typeof(Camera));
            cameraGo.tag = "MainCamera";
            cameraGo.transform.SetParent(transform, worldPositionStays: false);

            var camera = cameraGo.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.01f, 0.06f);
            camera.fieldOfView = 62f;
            camera.nearClipPlane = 0.3f;

            // Far plane is tight on purpose: it must reach the furthest chunk and no further. Every
            // extra metre is geometry the GPU considers and then discards.
            camera.farClipPlane = _tuning.ChunkLength * (_tuning.ActiveChunks + 1);

            _camera = new RunCameraRig(cameraGo.transform, playerPivot, camera);

            // --- Lighting -------------------------------------------------------------------
            var lightGo = new GameObject("KeyLight", typeof(Light));
            lightGo.transform.SetParent(transform, worldPositionStays: false);
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.color = new Color(0.6f, 0.75f, 1f);

            // Real-time shadows are the single most expensive thing a mobile renderer can be asked
            // for, and at speed nobody reads them. The neon look is carried by emission, not by
            // lighting.
            light.shadows = LightShadows.None;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.12f, 0.10f, 0.25f);

            // --- UI -------------------------------------------------------------------------
            var uiRoot = new GameObject("UI").transform;
            uiRoot.SetParent(transform, worldPositionStays: false);

            EnsureEventSystem(uiRoot);

            _hud = new RunHud(_session, _bus, uiRoot, _wallet, _adDirector);

            _storeScreen = new StoreScreen(_catalog, _store, _wallet, _inventory, /*iap*/ ResolveIap(), _bus, uiRoot);

            // The SHOP button lives on the death screen (the natural spend moment); tapping it opens
            // the store overlay. The store closes itself via its own CLOSE button.
            _hud.ShopRequested += () => _storeScreen.Show();

            _input = new SwipeInput();
        }

        private IIapService ResolveIap() => _container.Resolve<IIapService>();

        /// <summary>
        /// Creates the UI EventSystem, if the scene has none.
        ///
        /// This is required for a single, easily-missed reason: the project uses the new Input System
        /// package, and the classic <c>StandaloneInputModule</c> silently does nothing under it. UI
        /// buttons would render but never receive a click, and the bug looks like "my button is
        /// broken" rather than "the input module is wrong". The store is the first screen with real
        /// buttons, so this is where the EventSystem earns its place.
        /// </summary>
        private static void EnsureEventSystem(Transform parent)
        {
            if (EventSystem.current != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(parent, worldPositionStays: false);

            // Without default actions the module initialises but routes nothing. AssignDefaultActions
            // wires up point/click/scroll so buttons work with no hand-authored input asset.
            go.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }

        /// <summary>
        /// Locks the frame rate target and disables the frame-pacing traps that silently cost a
        /// mobile runner its 60 fps.
        /// </summary>
        private static void ConfigureQualityForMobile()
        {
            // vSync must be off for targetFrameRate to have any effect at all — with vSync on,
            // Unity ignores targetFrameRate entirely and this is a very common shipped bug.
            QualitySettings.vSyncCount = 0;
            UnityEngine.Application.targetFrameRate = 60;

            // Never let the phone dim or sleep mid-run.
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        private void StartRun()
        {
            _states.TransitionTo(GameState.Playing);

            _player.Reset();
            _track.Reset();
            _camera.Reset();
            _session.Begin();

            _awaitingRestart = false;
        }

        private void OnRunEnded(RunEnded e)
        {
            _states.TransitionTo(GameState.GameOver);

            // Only a crash shakes the camera. Quitting deliberately is not an impact, and shaking
            // the screen at a player who chose to leave is noise pretending to be feedback.
            if (e.Cause == DeathCause.HitObstacle)
            {
                _camera.Shake();
            }

            _awaitingRestart = true;

            // Arm the restart slightly in the future. Without this, the very swipe or tap that was
            // in flight when the player died is still "pressed" on the next frame and instantly
            // restarts the run, so the player never sees their score.
            _restartArmedAt = Time.unscaledTime + RestartArmDelay;

            Debug.Log(
                $"[NeonRush] Run {e.RunNumber} ended: {(int)e.DistanceMetres} m, " +
                $"{e.CoinsCollected} coins, score {e.Score}, {e.DurationSeconds:F1}s, cause {e.Cause}.");

            if (_track.PoolsGrewUnderLoad)
            {
                // A pool that grew mid-run means the player paid for an Instantiate during
                // gameplay. It is a real defect, not a curiosity, so it is reported as a warning.
                Debug.LogWarning(
                    "[NeonRush] Object pools grew during the run — pre-warm counts are too low. " +
                    "This causes a GC spike mid-run. Raise the prewarm values in TrackStreamer.");
            }
        }

        private const float RestartArmDelay = 0.35f;

        /// <summary>
        /// The app is going to the background (or coming back).
        ///
        /// This is the single most important line of persistence code in the project. Android and
        /// iOS terminate backgrounded apps whenever they feel like it, with no further warning and
        /// without running another line of our code. <c>OnApplicationPause(true)</c> is the last
        /// moment we are guaranteed to execute — so the write happens here, synchronously. Anything
        /// deferred past this point may simply never run, and the player loses their session.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (!paused) return;

            _save?.Flush();
        }

        /// <summary>
        /// A clean exit. Reached on desktop and sometimes on mobile — but never rely on it there,
        /// which is why <see cref="OnApplicationPause"/> carries the real weight.
        /// </summary>
        private void OnApplicationQuit()
        {
            _save?.Flush();
        }

        private void Update()
        {
            var deltaTime = Time.deltaTime;
            var command = _input.Poll();

            // Outside the switch: the debounced save must keep ticking regardless of game state.
            // A player sitting on the game-over screen with unsaved coins is exactly the player
            // most likely to background the app and never come back.
            _save.Tick(deltaTime);

            switch (_states.Current)
            {
                case GameState.Playing:
                    TickRun(deltaTime, command);
                    break;

                case GameState.GameOver:
                    TickGameOver(deltaTime, command);
                    break;

                case GameState.Boot:
                case GameState.MainMenu:
                case GameState.Paused:
                default:
                    break;
            }
        }

        /// <summary>
        /// One frame of gameplay. The order here is the contract, and every line of it matters:
        ///
        ///  1. The player acts on this frame's input.
        ///  2. The session advances distance and speed.
        ///  3. The world scrolls by that same speed.
        ///  4. Collision tests the state the player and world are actually in, now.
        ///  5. The camera follows where the player ended up.
        ///  6. The HUD reports it.
        ///
        /// Moving collision before the world scroll, for instance, would test the player against
        /// last frame's obstacle positions — producing hits that visibly did not happen and misses
        /// that visibly did.
        /// </summary>
        private void TickRun(float deltaTime, SwipeCommand command)
        {
            _player.Tick(deltaTime, command);
            _session.Tick(deltaTime);
            _track.Tick(deltaTime, _session.Speed);
            _collisions.Tick();
            _camera.Tick(deltaTime, NormalisedSpeed);
            _hud.Tick();
        }

        /// <summary>0 at base speed, 1 at the speed cap. Drives the camera's speed-FOV.</summary>
        private float NormalisedSpeed
        {
            get
            {
                var range = _tuning.MaxSpeed - _tuning.BaseSpeed;
                return range <= 0f ? 0f : Mathf.Clamp01((_session.Speed - _tuning.BaseSpeed) / range);
            }
        }

        private void TickGameOver(float deltaTime, SwipeCommand command)
        {
            // The camera keeps ticking after death so the impact shake can actually play out. A
            // shake that is started and then never advanced is just a frozen, crooked camera.
            _camera.Tick(deltaTime, NormalisedSpeed);

            if (!_awaitingRestart) return;
            if (_adDirector.IsAdInFlight) return;

            // While the store overlay is up, the death-screen gestures are suspended entirely. A
            // swipe meant to scroll the shop must never also revive or restart the run behind it.
            if (_storeScreen.IsOpen) return;

            if (Time.unscaledTime < _restartArmedAt) return;

            // Swipe up = revive (watch an ad). Deliberately a distinct gesture from the tap that
            // dismisses the screen: a player who is jabbing at the screen in frustration must not
            // trigger a 30-second ad they never asked for.
            if (command == SwipeCommand.Up && _adDirector.CanOfferRevive(_session.RevivesUsed))
            {
                OfferRevive();
                return;
            }

            // Swipe down = double the coins you just earned. Also a distinct gesture, for the same
            // reason. Note this is purely additive: the coins are already banked, so declining costs
            // the player nothing. See RunRewardService.
            if (command == SwipeCommand.Down && _adDirector.CanOfferDoubleCoins)
            {
                OfferDoubleCoins();
                return;
            }

            // A tap that landed on a UI button (the SHOP button) must not ALSO restart the run. The
            // EventSystem routes the click to the button; this check stops the same tap from being
            // read a second time as "dismiss the death screen".
            if (IsPointerOverUi()) return;

            if (command != SwipeCommand.None || WasTapped())
            {
                LeaveGameOver();
            }
        }

        private static bool IsPointerOverUi() =>
            EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        private void OfferDoubleCoins()
        {
            _awaitingRestart = false;

            _adDirector.OfferDoubleCoins(granted =>
            {
                if (granted > 0)
                {
                    Debug.Log($"[NeonRush] Rewarded ad doubled the run: +{granted} coins.");
                }

                // Either way, back to the game-over screen. The doubling offer is gone (it can only
                // be claimed once), but nothing was taken away.
                _awaitingRestart = true;
                _restartArmedAt = Time.unscaledTime + RestartArmDelay;

                _hud.RefreshGameOverOffers();
            });
        }

        private void OfferRevive()
        {
            _awaitingRestart = false;

            _adDirector.OfferRevive(_session.RevivesUsed, watched =>
            {
                if (!watched)
                {
                    // No ad, or they closed it early. Nothing is granted, and we simply return to the
                    // game-over screen — never punish a failed ad load by taking something away.
                    _awaitingRestart = true;
                    _restartArmedAt = Time.unscaledTime + RestartArmDelay;
                    return;
                }

                // Clear the wall they just died in BEFORE resuming. Reviving into the obstacle that
                // killed you, and dying again on the next frame, turns a paid revive into a swindle.
                _track.ClearObstaclesNear(ReviveClearRadius);

                _player.Reset();
                _session.Revive();

                _states.TransitionTo(GameState.Playing);
            });
        }

        /// <summary>Metres of track cleared around the player on revive. Generous on purpose — see TrackStreamer.</summary>
        private const float ReviveClearRadius = 30f;

        private void LeaveGameOver()
        {
            _awaitingRestart = false;

            // The interstitial is offered here and NOWHERE else in the codebase. That is what makes
            // "an ad can never interrupt gameplay" a structural fact rather than a rule someone has
            // to remember.
            _adDirector.MaybeShowInterstitial(_session.Duration, () =>
            {
                _states.TransitionTo(GameState.MainMenu);
                StartRun();
            });
        }

        private static bool WasTapped()
        {
            var touchscreen = UnityEngine.InputSystem.Touchscreen.current;
            if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame) return true;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) return true;

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.spaceKey.wasPressedThisFrame;
        }

        private void OnDestroy()
        {
            // Teardown order mirrors construction. Anything holding an event subscription must let
            // go of it before the bus dies, or the bus keeps the subscriber alive and the "leak"
            // shows up as a second HUD reacting to the next run.
            _runEndedSubscription?.Dispose();

            // SaveService.Dispose flushes on the way out, so it must run before the wallet and the
            // bus it reads from are torn down.
            _save?.Dispose();

            _adDirector?.Dispose();
            _analyticsReporter?.Dispose();
            _missions?.Dispose();
            _profile?.Dispose();
            _rewards?.Dispose();
            _storeScreen?.Dispose();
            _hud?.Dispose();
            _track?.Dispose();
            _materials?.Dispose();
            _container?.Dispose();
            _bus?.Dispose();
        }
    }
}
