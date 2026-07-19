using System;
using System.Collections.Generic;
using NeonRush.Application.Events;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Audio;
using NeonRush.Domain.Ports;
using NeonRush.Domain.PowerUps;

namespace NeonRush.Application.Audio
{
    /// <summary>
    /// Decides <em>when</em> the game makes a sound, by listening to the events it already publishes
    /// and translating each into a <see cref="SoundId"/>. It is the audio twin of
    /// <see cref="Analytics.AnalyticsReporter"/>: one subscriber that turns the existing event stream
    /// into another output channel, so no gameplay system ever calls the audio engine directly and the
    /// "what plays when" rules live in exactly one testable place.
    ///
    /// Engine-free on purpose. It depends on the <see cref="IAudioService"/> port, never on Unity, so
    /// the entire mapping — coin makes the coin sound, only an obstacle death crashes, a saved shield
    /// shatters — is asserted against a fake in a unit test rather than verified by ear.
    /// </summary>
    public sealed class AudioReporter : IDisposable
    {
        private readonly IAudioService _audio;
        private readonly List<IDisposable> _subscriptions = new();

        public AudioReporter(IAudioService audio, IEventBus bus)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
            if (bus == null) throw new ArgumentNullException(nameof(bus));

            _subscriptions.Add(bus.Subscribe<RunStarted>(_ => _audio.Play(SoundId.RunStart)));
            _subscriptions.Add(bus.Subscribe<CoinCollected>(_ => _audio.Play(SoundId.Coin)));
            _subscriptions.Add(bus.Subscribe<PlayerJumped>(_ => _audio.Play(SoundId.Jump)));
            _subscriptions.Add(bus.Subscribe<PlayerSlid>(_ => _audio.Play(SoundId.Slide)));
            _subscriptions.Add(bus.Subscribe<DistanceMilestone>(_ => _audio.Play(SoundId.Milestone)));

            // Only a real death crashes. A deliberate quit is not an impact, and slamming a crash sound
            // at a player who chose to leave is noise pretending to be feedback — the same reasoning the
            // camera shake follows.
            _subscriptions.Add(bus.Subscribe<RunEnded>(e =>
            {
                if (e.Cause == DeathCause.HitObstacle) _audio.Play(SoundId.Crash);
            }));

            // Power-ups: the pickup chimes; a shield absorbing a hit shatters. The shield sound is a
            // reward cue, played instead of the crash — the crash never fires because the run did not
            // end (the shield swallowed the hit before End was called).
            _subscriptions.Add(bus.Subscribe<PowerUpCollected>(_ => _audio.Play(SoundId.PowerUp)));
            _subscriptions.Add(bus.Subscribe<ShieldConsumed>(_ => _audio.Play(SoundId.ShieldBreak)));

            // A completed purchase or claim confirms audibly.
            _subscriptions.Add(bus.Subscribe<PurchaseCompleted>(_ => _audio.Play(SoundId.Confirm)));
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
