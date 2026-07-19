namespace NeonRush.Domain.Audio
{
    /// <summary>
    /// Every distinct sound the game can make. An id, not a clip: the mapping from id to actual audio
    /// samples lives in <see cref="SoundBank"/>, and the code that decides <em>when</em> to play a
    /// sound (<see cref="Application.Audio.AudioReporter"/>) only ever names the id. That indirection
    /// is what lets the whole "what plays when" layer be unit-tested without an audio engine, and lets
    /// the actual sound of, say, a coin be retuned in one place without touching the event wiring.
    /// </summary>
    public enum SoundId
    {
        /// <summary>A coin was collected. Bright, short, high.</summary>
        Coin = 0,

        /// <summary>The player jumped. A quick upward chirp.</summary>
        Jump = 1,

        /// <summary>The player slid. A short downward whoosh.</summary>
        Slide = 2,

        /// <summary>The player hit an obstacle and the run ended. A harsh downward crash.</summary>
        Crash = 3,

        /// <summary>A power-up was picked up. A rising arcade chime.</summary>
        PowerUp = 4,

        /// <summary>A shield absorbed a hit. A bright shatter — reward, not punishment.</summary>
        ShieldBreak = 5,

        /// <summary>A 100 m milestone was crossed. A subtle, encouraging ping.</summary>
        Milestone = 6,

        /// <summary>A UI button was pressed. A soft click.</summary>
        Button = 7,

        /// <summary>A purchase or claim completed. A satisfying two-note confirm.</summary>
        Confirm = 8,

        /// <summary>A run began. A short upward sweep that launches the player forward.</summary>
        RunStart = 9,
    }
}
