using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Stages;
using NeonRush.Core.Events;
using NeonRush.Domain.PowerUps;
using NeonRush.Presentation.Visuals;
using UnityEngine;

namespace NeonRush.Presentation.View
{
    /// <summary>
    /// World-space particle bursts: debris on a crash, a sparkle on a coin, a flash on a power-up.
    ///
    /// The events that trigger these carry no position, but they do not need to: everything they mark
    /// happens at the player, who is fixed at world z = 0. So each burst is emitted at the player's
    /// position and nobody has to thread a Vector3 through the event bus.
    ///
    /// One <see cref="ParticleSystem"/> per colour, each reusing a <see cref="NeonMaterials"/> Lit
    /// material — the same shader path already pinned into the build, so nothing here can be stripped
    /// on device the way a bespoke particle shader would be. Bursts only, no continuous emission, so
    /// the cost is a handful of quads for half a second at the exact moments the eye is on the action.
    /// </summary>
    public sealed class RunParticles : IDisposable
    {
        private readonly Transform _player;
        private readonly List<IDisposable> _subscriptions = new();

        private readonly ParticleSystem _crash;
        private readonly ParticleSystem _coin;
        private readonly ParticleSystem _powerUp;

        /// <summary>Bursts spawn at roughly the player's centre of mass, not at their feet.</summary>
        private static readonly Vector3 ChestOffset = new(0f, 0.9f, 0f);

        public RunParticles(Transform parent, NeonMaterials materials, IEventBus bus, Transform player)
        {
            _player = player;

            // Danger colour for the crash, reward gold for coins, cyan energy for power-ups — the same
            // palette the rest of the game reads by.
            _crash = BuildSystem(parent, "CrashBurst", materials.Get(NeonMaterials.Obstacle, emission: 3f),
                size: 0.22f, speed: 7f, lifetime: 0.55f, gravity: 1.6f);

            _coin = BuildSystem(parent, "CoinSparkle", materials.Get(NeonMaterials.Coin, emission: 3f),
                size: 0.12f, speed: 3f, lifetime: 0.35f, gravity: 0.2f);

            _powerUp = BuildSystem(parent, "PowerUpBurst", materials.Get(new Color(0.4f, 1f, 0.95f), emission: 3f),
                size: 0.18f, speed: 5f, lifetime: 0.5f, gravity: 0.3f);

            // A crash is the one to skip on a deliberate quit — see OnRunEnded.
            _subscriptions.Add(bus.Subscribe<RunEnded>(OnRunEnded));
            _subscriptions.Add(bus.Subscribe<CoinCollected>(_ => Burst(_coin, 5)));
            _subscriptions.Add(bus.Subscribe<PowerUpCollected>(_ => Burst(_powerUp, 18)));

            // The shield saving you deserves its own pop — reuse the power-up burst, bigger.
            _subscriptions.Add(bus.Subscribe<ShieldConsumed>(_ => Burst(_powerUp, 26)));

            // Clearing a stage mid-run throws a big celebratory shower.
            _subscriptions.Add(bus.Subscribe<StageCompleted>(_ => Burst(_powerUp, 40)));
        }

        private void OnRunEnded(RunEnded e)
        {
            // Only a real impact throws debris. Quitting is not a crash.
            if (e.Cause == DeathCause.HitObstacle) Burst(_crash, 28);
        }

        private void Burst(ParticleSystem system, int count)
        {
            system.transform.position = _player.position + ChestOffset;
            system.Emit(count);
        }

        private static ParticleSystem BuildSystem(
            Transform parent, string name, Material material,
            float size, float speed, float lifetime, float gravity)
        {
            var go = new GameObject(name, typeof(ParticleSystem));
            go.transform.SetParent(parent, worldPositionStays: false);

            var system = go.GetComponent<ParticleSystem>();

            var main = system.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = size;
            main.gravityModifier = gravity;
            main.maxParticles = 64;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // debris keeps flying as the world scrolls

            // Bursts only: emission is driven by Emit() calls, never a continuous rate.
            var emission = system.emission;
            emission.enabled = false;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.15f;

            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.material = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return system;
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
