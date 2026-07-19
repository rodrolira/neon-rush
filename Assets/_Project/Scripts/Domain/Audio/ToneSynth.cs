using System;

namespace NeonRush.Domain.Audio
{
    /// <summary>
    /// Turns a <see cref="SoundSpec"/> into raw mono samples. Pure maths, no Unity, no allocation
    /// beyond the returned buffer — which is exactly why it lives in Domain and is unit-tested: the
    /// two properties that actually matter for audio quality (the samples never exceed ±1, and the
    /// sound starts and ends at silence so it does not click) are asserted in tests rather than
    /// discovered by ear on a device.
    ///
    /// The envelope is a simple pluck: a short linear attack out of silence, then an exponential
    /// decay, then a final hard fade to zero over the last few milliseconds so the buffer always ends
    /// exactly at silence regardless of the decay constant. That last fade is the difference between a
    /// clean blip and an audible pop at the end of every single sound.
    /// </summary>
    public static class ToneSynth
    {
        /// <summary>Length of the guaranteed fade-to-zero at the tail, in seconds. Short enough to be inaudible as a fade, long enough to kill the click.</summary>
        private const float TailFadeSeconds = 0.004f;

        /// <summary>
        /// Renders the spec to a mono buffer at the given sample rate. Deterministic: the same spec
        /// and rate always produce byte-identical samples, including the noise, so a test can assert on
        /// exact output and a run can be reasoned about.
        /// </summary>
        public static float[] Render(SoundSpec spec, int sampleRate)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            var count = Math.Max(1, (int)Math.Round(spec.DurationSeconds * sampleRate));
            var samples = new float[count];

            // Exponential decay tuned so amplitude reaches ~0.7% of peak by the end of the sound,
            // independent of duration — a short sound and a long sound have the same *shape* of decay.
            var decayRate = spec.DurationSeconds > 0f ? 5f / spec.DurationSeconds : 0f;

            var tailFadeSamples = Math.Max(1, (int)Math.Round(TailFadeSeconds * sampleRate));

            // Deterministic noise source (xorshift), seeded from a constant so output is reproducible.
            uint noise = 0x9E3779B9u;

            var phase = 0.0; // radians, accumulated so a frequency sweep stays continuous

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)sampleRate;
                var frac = count > 1 ? i / (float)(count - 1) : 0f;

                var frequency = Lerp(spec.StartFrequency, spec.EndFrequency, frac);

                float wave;
                if (spec.Waveform == Waveform.Noise)
                {
                    noise ^= noise << 13;
                    noise ^= noise >> 17;
                    noise ^= noise << 5;
                    wave = noise / (float)uint.MaxValue * 2f - 1f; // [-1, 1]
                }
                else
                {
                    phase += 2.0 * Math.PI * frequency / sampleRate;
                    var s = Math.Sin(phase);

                    wave = spec.Waveform switch
                    {
                        Waveform.Square => s >= 0.0 ? 1f : -1f,
                        Waveform.Triangle => (float)(2.0 / Math.PI * Math.Asin(s)),
                        _ => (float)s, // Sine
                    };
                }

                // Envelope: attack out of silence, then exponential decay.
                var attack = spec.AttackSeconds > 0f ? Clamp01(t / spec.AttackSeconds) : 1f;
                var envelope = attack * (float)Math.Exp(-decayRate * t);

                // Final fade so the buffer always ends exactly at silence — no end-of-clip click.
                var fromEnd = count - 1 - i;
                if (fromEnd < tailFadeSamples)
                {
                    envelope *= fromEnd / (float)tailFadeSamples;
                }

                samples[i] = Clamp(wave * envelope * spec.Volume, -1f, 1f);
            }

            return samples;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
