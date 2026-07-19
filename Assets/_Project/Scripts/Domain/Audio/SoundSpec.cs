namespace NeonRush.Domain.Audio
{
    /// <summary>The oscillator shape a <see cref="SoundSpec"/> is built from.</summary>
    public enum Waveform
    {
        /// <summary>Pure tone. Soft and clean — coins, pings.</summary>
        Sine = 0,

        /// <summary>Hollow, buzzy, retro. Chiptune blips.</summary>
        Square = 1,

        /// <summary>Between sine and square — a bit of bite without the harshness. Jumps.</summary>
        Triangle = 2,

        /// <summary>White noise. The basis of whooshes, crashes and shatters.</summary>
        Noise = 3,
    }

    /// <summary>
    /// A pure description of one sound: a single oscillator sweeping between two frequencies under a
    /// pluck envelope. It carries no samples and no Unity types — it is the recipe, not the audio.
    /// <see cref="ToneSynth"/> turns it into samples; the presentation layer turns those into a clip.
    ///
    /// One oscillator per sound is a deliberate limit. Layered synthesis sounds richer but is far
    /// harder to keep from sounding like mud on a phone speaker, and the arcade feel this game wants —
    /// short, legible, punchy blips — is exactly what a single swept oscillator with a fast decay does
    /// best. It is also trivially testable, which a multi-voice graph is not.
    /// </summary>
    public readonly struct SoundSpec
    {
        public readonly Waveform Waveform;

        /// <summary>Frequency at the start of the sound, in hertz.</summary>
        public readonly float StartFrequency;

        /// <summary>Frequency at the end. Different from the start makes the tone sweep — up chirps, down crashes.</summary>
        public readonly float EndFrequency;

        /// <summary>How long the sound lasts, in seconds. Short: these are feedback blips, not music.</summary>
        public readonly float DurationSeconds;

        /// <summary>Fade-in time, in seconds. A tiny non-zero attack removes the click a hard start makes.</summary>
        public readonly float AttackSeconds;

        /// <summary>Peak amplitude, 0..1. Headroom below 1 so layered simultaneous sounds do not clip.</summary>
        public readonly float Volume;

        public SoundSpec(Waveform waveform, float startFrequency, float endFrequency, float durationSeconds, float attackSeconds, float volume)
        {
            Waveform = waveform;
            StartFrequency = startFrequency;
            EndFrequency = endFrequency;
            DurationSeconds = durationSeconds;
            AttackSeconds = attackSeconds;
            Volume = volume;
        }
    }
}
