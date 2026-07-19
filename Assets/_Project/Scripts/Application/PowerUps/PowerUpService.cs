using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Run;
using NeonRush.Core.Events;
using NeonRush.Domain.PowerUps;

namespace NeonRush.Application.PowerUps
{
    /// <summary>
    /// Turns a pickup into an effect, and keeps that effect running.
    ///
    /// It owns the pure <see cref="PowerUpState"/> and does the three jobs that need the rest of the
    /// game: it activates the right effect when a pickup is grabbed, it drives the run's score
    /// multiplier while DoubleScore is up, and it answers the one life-or-death question the collision
    /// system asks — "is there a shield to spend?". Everything it publishes is for the HUD, audio and
    /// analytics, none of which know about each other.
    ///
    /// No Unity types: the effect logic is unit-tested without a scene. The magnet's *visual* pull
    /// lives in the presentation layer (TrackStreamer), which reads <see cref="IsMagnetActive"/>; this
    /// service only decides whether the magnet is on.
    /// </summary>
    public sealed class PowerUpService : IDisposable
    {
        private readonly IEventBus _bus;
        private readonly RunSession _session;
        private readonly PowerUpConfig _config;
        private readonly PowerUpState _state = new();
        private readonly List<IDisposable> _subscriptions = new();

        public PowerUpService(IEventBus bus, RunSession session, PowerUpConfig config)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _config.Validate();

            // A new run starts clean; a finished run must not carry a live magnet into the menu.
            _subscriptions.Add(bus.Subscribe<RunStarted>(_ => ResetForRun()));
            _subscriptions.Add(bus.Subscribe<RunEnded>(_ => ResetForRun()));
        }

        /// <summary>True while the coin magnet is pulling. Read every frame by the coin mover.</summary>
        public bool IsMagnetActive => _state.IsMagnetActive;

        /// <summary>True while the score multiplier is running.</summary>
        public bool IsDoubleScoreActive => _state.IsDoubleScoreActive;

        /// <summary>Shield charges banked. Each absorbs one obstacle hit.</summary>
        public int ShieldCharges => _state.ShieldCharges;

        /// <summary>Seconds of magnet left. For the HUD.</summary>
        public float MagnetRemaining => _state.MagnetRemaining;

        /// <summary>Seconds of score multiplier left. For the HUD.</summary>
        public float DoubleScoreRemaining => _state.DoubleScoreRemaining;

        /// <summary>
        /// Applies a grabbed pickup. Publishes the grab, activates the effect, and announces it so the
        /// HUD can draw it. A no-op if power-ups are disabled by config.
        /// </summary>
        public void Collect(PowerUpType type)
        {
            if (!_config.Enabled) return;

            _bus.Publish(new PowerUpCollected(type));

            switch (type)
            {
                case PowerUpType.Magnet:
                    _state.ActivateMagnet(_config.MagnetSeconds);
                    _bus.Publish(new PowerUpActivated(type, _config.MagnetSeconds, 0));
                    break;

                case PowerUpType.DoubleScore:
                    _state.ActivateDoubleScore(_config.DoubleScoreSeconds);
                    ApplyScoreMultiplier();
                    _bus.Publish(new PowerUpActivated(type, _config.DoubleScoreSeconds, 0));
                    break;

                case PowerUpType.Shield:
                    _state.AddShield(_config.ShieldChargesPerPickup);
                    _bus.Publish(new PowerUpActivated(type, 0f, _state.ShieldCharges));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown power-up type.");
            }
        }

        /// <summary>
        /// Spends a shield charge to absorb an obstacle hit. Returns true when the hit is absorbed
        /// (the player survives), false when there was no charge (the hit is fatal). The collision
        /// system calls this at the exact moment of impact.
        /// </summary>
        public bool TryConsumeShield()
        {
            if (!_state.TryConsumeShield()) return false;

            _bus.Publish(new ShieldConsumed(_state.ShieldCharges));
            return true;
        }

        /// <summary>
        /// Runs the timed effects down by one frame and keeps the run's score multiplier in sync.
        /// Called once per frame from the game loop.
        /// </summary>
        public void Tick(float deltaTime)
        {
            // Sample before AND after the tick, so an effect that both is active and expires within a
            // single (possibly large) delta still announces exactly one expiry. Remembering the flag
            // across frames instead would miss the case where a hitch-sized delta ends the effect on
            // the same frame everything else sees it.
            var magnetWasActive = _state.IsMagnetActive;
            var doubleScoreWasActive = _state.IsDoubleScoreActive;

            _state.Tick(deltaTime);

            if (magnetWasActive && !_state.IsMagnetActive)
            {
                _bus.Publish(new PowerUpExpired(PowerUpType.Magnet));
            }

            if (doubleScoreWasActive && !_state.IsDoubleScoreActive)
            {
                ApplyScoreMultiplier(); // back to 1×
                _bus.Publish(new PowerUpExpired(PowerUpType.DoubleScore));
            }
        }

        private void ApplyScoreMultiplier()
        {
            _session.ScoreMultiplier = _state.IsDoubleScoreActive ? _config.ScoreMultiplier : 1f;
        }

        private void ResetForRun()
        {
            _state.Reset();
            _session.ScoreMultiplier = 1f;
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
