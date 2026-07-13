using System;
using NeonRush.Application.Events;
using NeonRush.Application.Run;
using NeonRush.Application.States;
using NeonRush.Core.DI;
using NeonRush.Core.Events;
using NeonRush.Domain.Ports;
using NeonRush.Domain.Run;
using NeonRush.Infrastructure.Time;
using NeonRush.Presentation.Input;
using NeonRush.Presentation.Player;
using NeonRush.Presentation.View;
using NeonRush.Presentation.Visuals;
using NeonRush.Presentation.World;
using UnityEngine;

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

        private Container _container;
        private EventBus _bus;
        private GameStateMachine _states;
        private RunSession _session;
        private RunTuning _tuning;

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

            _tuning = new RunTuning();

            _bus = new EventBus();
            _states = new GameStateMachine();

            _container = new Container();
            _container.RegisterInstance<IEventBus>(_bus);
            _container.RegisterInstance<IClock>(new SystemClock());
            _container.RegisterInstance(_tuning);
            _container.RegisterInstance(_states);

            _session = new RunSession(_tuning, _bus);
            _container.RegisterInstance(_session);

            BuildScene();

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

            _collisions = new CollisionSystem(_track, _player, _session);

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

            _camera = new RunCameraRig(cameraGo.transform, playerPivot);

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

            _hud = new RunHud(_session, _bus, uiRoot);

            _input = new SwipeInput();
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

        private void Update()
        {
            var deltaTime = Time.deltaTime;
            var command = _input.Poll();

            switch (_states.Current)
            {
                case GameState.Playing:
                    TickRun(deltaTime, command);
                    break;

                case GameState.GameOver:
                    TickGameOver(command);
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
            _camera.Tick(deltaTime);
            _hud.Tick();
        }

        private void TickGameOver(SwipeCommand command)
        {
            if (!_awaitingRestart) return;
            if (Time.unscaledTime < _restartArmedAt) return;

            if (command != SwipeCommand.None || WasTapped())
            {
                _states.TransitionTo(GameState.MainMenu);
                StartRun();
            }
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

            _hud?.Dispose();
            _track?.Dispose();
            _materials?.Dispose();
            _container?.Dispose();
            _bus?.Dispose();
        }
    }
}
