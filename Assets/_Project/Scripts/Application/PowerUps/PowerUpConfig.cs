using System;

namespace NeonRush.Application.PowerUps
{
    /// <summary>
    /// Every number that tunes the power-ups. Like every other config in the game it starts from
    /// balanced compiled defaults and is overridden — within clamped ranges — by Remote Config, so a
    /// power-up that turns out to be too strong or too rare can be retuned for a live cohort without a
    /// store submission. See <see cref="Config.GameConfigService"/> for the clamping.
    ///
    /// Plain get/set, immutable by convention (built once at boot): Unity 6 on .NET Standard 2.1 has
    /// no <c>init</c>, the same reason <see cref="Domain.Run.RunTuning"/> gives.
    /// </summary>
    public sealed class PowerUpConfig
    {
        /// <summary>Master switch. Lets LiveOps pull power-ups entirely if one misbehaves.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>How long the coin magnet pulls, in seconds.</summary>
        public float MagnetSeconds { get; set; } = 6f;

        /// <summary>How long the score multiplier runs, in seconds.</summary>
        public float DoubleScoreSeconds { get; set; } = 8f;

        /// <summary>The multiplier applied to the distance score while DoubleScore is active.</summary>
        public float ScoreMultiplier { get; set; } = 2f;

        /// <summary>Shield charges granted per pickup. One hit absorbed each.</summary>
        public int ShieldChargesPerPickup { get; set; } = 1;

        /// <summary>Radius within which the magnet grabs coins, in metres.</summary>
        public float MagnetRadius { get; set; } = 6f;

        /// <summary>How fast the magnet reels a coin in, in metres/second.</summary>
        public float MagnetPullSpeed { get; set; } = 22f;

        /// <summary>
        /// Probability that a given track chunk carries one pickup. Deliberately well below 1: a
        /// power-up is a treat, and a treat on every chunk stops being one — it also trivialises the
        /// difficulty the rest of the game works to build. Tuned down from 0.35 on a device playtest
        /// where pickups felt too frequent; at 0.18 one lands roughly every ~160 m.
        /// </summary>
        public float SpawnChancePerChunk { get; set; } = 0.18f;

        /// <summary>Throws on values that would make a power-up meaningless or breaking.</summary>
        public void Validate()
        {
            if (MagnetSeconds <= 0f) throw new ArgumentException($"{nameof(MagnetSeconds)} must be > 0.");
            if (DoubleScoreSeconds <= 0f) throw new ArgumentException($"{nameof(DoubleScoreSeconds)} must be > 0.");
            if (ScoreMultiplier < 1f) throw new ArgumentException($"{nameof(ScoreMultiplier)} must be >= 1.");
            if (ShieldChargesPerPickup < 1) throw new ArgumentException($"{nameof(ShieldChargesPerPickup)} must be >= 1.");
            if (MagnetRadius <= 0f) throw new ArgumentException($"{nameof(MagnetRadius)} must be > 0.");
            if (MagnetPullSpeed <= 0f) throw new ArgumentException($"{nameof(MagnetPullSpeed)} must be > 0.");
            if (SpawnChancePerChunk is < 0f or > 1f) throw new ArgumentException($"{nameof(SpawnChancePerChunk)} must be in [0,1].");
        }
    }
}
