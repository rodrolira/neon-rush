using NeonRush.Domain.Audio;
using NeonRush.Domain.Ports;

namespace NeonRush.Infrastructure.Audio
{
    /// <summary>
    /// An audio service that makes no sound. Bound in the headless test runner and available as the
    /// "audio off" fallback, so that code which plays sounds runs identically whether or not there is
    /// an audio engine underneath it. It still honours <see cref="IsMuted"/> so a mute toggle behaves
    /// consistently even with no output.
    /// </summary>
    public sealed class NullAudioService : IAudioService
    {
        public void Play(SoundId sound)
        {
            // Intentionally silent.
        }

        public void SetMuted(bool muted) => IsMuted = muted;

        public bool IsMuted { get; private set; }
    }
}
