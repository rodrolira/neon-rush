using System;

namespace NeonRush.Domain.Audio
{
    /// <summary>
    /// The "instrument" for every <see cref="SoundId"/>: the one place that decides what a coin, a
    /// jump or a crash actually sounds like. Pure data — a table of recipes — so the whole palette can
    /// be retuned here without touching a line of engine code or event wiring.
    ///
    /// The choices encode a little sound-design grammar the player reads without noticing:
    ///  · <b>Up-sweeps</b> mean gain — coins, power-ups, jumps, the run starting. Good things rise.
    ///  · <b>Down-sweeps</b> mean loss or impact — the crash falls.
    ///  · <b>Noise</b> means physical friction or breakage — the slide hiss, the shield shatter.
    ///  · <b>Pure sine pings</b> are neutral status — milestones, UI.
    /// </summary>
    public static class SoundBank
    {
        /// <summary>Returns the sound recipe for an id.</summary>
        public static SoundSpec For(SoundId id) => id switch
        {
            // Bright, short, rising — the classic "coin" read.
            SoundId.Coin => new SoundSpec(Waveform.Square, 880f, 1320f, 0.09f, 0.002f, 0.35f),

            // Quick upward chirp: the body leaving the ground.
            SoundId.Jump => new SoundSpec(Waveform.Triangle, 320f, 720f, 0.14f, 0.004f, 0.45f),

            // A short downward hiss: friction with the floor.
            SoundId.Slide => new SoundSpec(Waveform.Noise, 0f, 0f, 0.20f, 0.004f, 0.35f),

            // Harsh, low, falling — impact and failure.
            SoundId.Crash => new SoundSpec(Waveform.Noise, 0f, 0f, 0.38f, 0.001f, 0.6f),

            // A confident rising arcade chime: you got the good thing.
            SoundId.PowerUp => new SoundSpec(Waveform.Square, 520f, 1560f, 0.26f, 0.004f, 0.4f),

            // A bright shatter — the shield saving you should feel like a reward, not a death.
            SoundId.ShieldBreak => new SoundSpec(Waveform.Noise, 0f, 0f, 0.28f, 0.001f, 0.5f),

            // A small, encouraging ping every 100 m.
            SoundId.Milestone => new SoundSpec(Waveform.Sine, 1180f, 1180f, 0.12f, 0.004f, 0.3f),

            // A soft, neutral UI click.
            SoundId.Button => new SoundSpec(Waveform.Sine, 660f, 660f, 0.05f, 0.003f, 0.3f),

            // A two-step rise for a completed purchase or claim — the "confirmed" feeling.
            SoundId.Confirm => new SoundSpec(Waveform.Triangle, 660f, 990f, 0.22f, 0.004f, 0.45f),

            // A quick upward sweep launching the run.
            SoundId.RunStart => new SoundSpec(Waveform.Triangle, 220f, 660f, 0.24f, 0.006f, 0.4f),

            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No sound recipe for this id."),
        };
    }
}
