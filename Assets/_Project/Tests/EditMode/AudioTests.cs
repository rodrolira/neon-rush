using System;
using System.Collections.Generic;
using NeonRush.Application.Audio;
using NeonRush.Application.Events;
using NeonRush.Application.Store;
using NeonRush.Core.Events;
using NeonRush.Domain.Audio;
using NeonRush.Domain.Ports;
using NeonRush.Domain.PowerUps;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the procedural synth — the maths that turns a recipe into samples. It checks the two
    /// things that actually make audio sound broken if they are wrong: samples that exceed ±1 (which
    /// clips and distorts) and a buffer that does not start or end at silence (which clicks on every
    /// single sound). Neither is something you want to be diagnosing by ear on a phone.
    /// </summary>
    [TestFixture]
    public sealed class ToneSynthTests
    {
        private const int Rate = 44100;

        [Test]
        public void Render_ProducesOneSamplePerFrameOfDuration()
        {
            var spec = new SoundSpec(Waveform.Sine, 440f, 440f, 0.1f, 0.004f, 0.5f);

            var samples = ToneSynth.Render(spec, Rate);

            Assert.That(samples.Length, Is.EqualTo((int)Math.Round(0.1f * Rate)));
        }

        [Test]
        public void Render_NeverExceedsTheUnitRange()
        {
            // Every recipe in the bank, since a too-hot one would clip. Noise is the usual culprit.
            foreach (SoundId id in Enum.GetValues(typeof(SoundId)))
            {
                var samples = ToneSynth.Render(SoundBank.For(id), Rate);

                foreach (var s in samples)
                {
                    Assert.That(s, Is.InRange(-1f, 1f), $"{id} produced a sample outside [-1,1].");
                }
            }
        }

        [Test]
        public void Render_StartsAndEndsAtSilence_SoItDoesNotClick()
        {
            foreach (SoundId id in Enum.GetValues(typeof(SoundId)))
            {
                var samples = ToneSynth.Render(SoundBank.For(id), Rate);

                Assert.That(Math.Abs(samples[0]), Is.LessThan(0.02f), $"{id} starts with a click.");
                Assert.That(Math.Abs(samples[^1]), Is.LessThan(0.02f), $"{id} ends with a click.");
            }
        }

        [Test]
        public void Render_IsDeterministic()
        {
            var spec = SoundBank.For(SoundId.Crash); // noise-based: the one most likely to differ if RNG leaks

            var a = ToneSynth.Render(spec, Rate);
            var b = ToneSynth.Render(spec, Rate);

            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void Render_RejectsANonPositiveSampleRate()
        {
            var spec = SoundBank.For(SoundId.Coin);
            Assert.That(() => ToneSynth.Render(spec, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void SoundBank_HasARecipeForEveryId()
        {
            foreach (SoundId id in Enum.GetValues(typeof(SoundId)))
            {
                Assert.That(() => SoundBank.For(id), Throws.Nothing, $"No recipe for {id}.");
            }
        }
    }

    /// <summary>
    /// Tests for the event→sound mapping. This is where the sound-design intent is enforced: a coin
    /// makes the coin sound, only a real death crashes, a saved shield shatters rather than crashing.
    /// Verified against a fake output so it needs no audio engine.
    /// </summary>
    [TestFixture]
    public sealed class AudioReporterTests
    {
        private sealed class FakeAudio : IAudioService
        {
            public readonly List<SoundId> Played = new();
            public void Play(SoundId sound) => Played.Add(sound);
            public void SetMuted(bool muted) => IsMuted = muted;
            public bool IsMuted { get; private set; }
        }

        private EventBus _bus;
        private FakeAudio _audio;
        private AudioReporter _reporter;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _audio = new FakeAudio();
            _reporter = new AudioReporter(_audio, _bus);
        }

        [TearDown]
        public void TearDown()
        {
            _reporter.Dispose();
            _bus.Dispose();
        }

        [Test]
        public void Coin_PlaysTheCoinSound()
        {
            _bus.Publish(new CoinCollected(1, 1));
            Assert.That(_audio.Played, Is.EqualTo(new[] { SoundId.Coin }));
        }

        [Test]
        public void JumpAndSlide_PlayTheirSounds()
        {
            _bus.Publish(new PlayerJumped());
            _bus.Publish(new PlayerSlid());

            Assert.That(_audio.Played, Is.EqualTo(new[] { SoundId.Jump, SoundId.Slide }));
        }

        [Test]
        public void HittingAnObstacle_Crashes()
        {
            _bus.Publish(new RunEnded(1, 100f, 5, 200, DeathCause.HitObstacle, 12f));
            Assert.That(_audio.Played, Is.EqualTo(new[] { SoundId.Crash }));
        }

        [Test]
        public void QuittingDoesNotCrash()
        {
            _bus.Publish(new RunEnded(1, 100f, 5, 200, DeathCause.Quit, 12f));
            Assert.That(_audio.Played, Is.Empty, "A deliberate quit is not an impact and must be silent.");
        }

        [Test]
        public void PowerUpPickup_Chimes()
        {
            _bus.Publish(new PowerUpCollected(PowerUpType.Magnet));
            Assert.That(_audio.Played, Is.EqualTo(new[] { SoundId.PowerUp }));
        }

        [Test]
        public void ShieldAbsorbingAHit_Shatters_NotCrashes()
        {
            // The shield swallows the hit before the run ends, so the crash never fires — the shatter
            // is what the player hears, a reward cue rather than a failure cue.
            _bus.Publish(new ShieldConsumed(0));
            Assert.That(_audio.Played, Is.EqualTo(new[] { SoundId.ShieldBreak }));
        }

        [Test]
        public void Milestone_RunStart_And_Purchase_MapToTheirSounds()
        {
            _bus.Publish(new RunStarted(1));
            _bus.Publish(new DistanceMilestone(100));
            _bus.Publish(new PurchaseCompleted("coins_small", false));

            Assert.That(_audio.Played, Is.EqualTo(new[] { SoundId.RunStart, SoundId.Milestone, SoundId.Confirm }));
        }
    }
}
