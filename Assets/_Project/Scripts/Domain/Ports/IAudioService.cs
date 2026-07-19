using NeonRush.Domain.Audio;

namespace NeonRush.Domain.Ports
{
    /// <summary>
    /// The game's one channel to the speakers. Everything that wants to make a sound names a
    /// <see cref="SoundId"/> and calls <see cref="Play"/>; nothing above this line knows how a clip is
    /// built or mixed.
    ///
    /// A port, exactly like <see cref="IAdsService"/>: the concrete implementation needs Unity's audio
    /// engine, so it lives in the presentation layer, while everything that decides *when* a sound
    /// plays stays engine-free and testable against a fake. The headless test runner and any future
    /// "audio off" build bind the null implementation and the rest of the game neither knows nor cares.
    /// </summary>
    public interface IAudioService
    {
        /// <summary>Plays a one-shot sound. Cheap and fire-and-forget; overlapping calls layer.</summary>
        void Play(SoundId sound);

        /// <summary>Silences (or restores) all audio at once — sound effects and music alike.</summary>
        void SetMuted(bool muted);

        /// <summary>True while everything is silenced.</summary>
        bool IsMuted { get; }
    }
}
