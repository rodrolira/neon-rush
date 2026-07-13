using System;

namespace NeonRush.Domain.Run
{
    /// <summary>
    /// Every number that defines how a run feels.
    ///
    /// This is a pure value object with no Unity dependency, which is what allows the difficulty
    /// curve to be unit-tested ("is the speed at 5 km still humanly reactable?") without opening
    /// the Editor. In the shipping game it is populated from a ScriptableObject whose fields are
    /// overridden by Remote Config — so difficulty can be retuned for a struggling cohort without
    /// a store submission. See ARCHITECTURE.md §9.
    ///
    /// The defaults below are a playable starting point, not a balanced game. Balance comes from
    /// telemetry (where do players die? at what distance do they quit?), not from intuition.
    /// </summary>
    public sealed class RunTuning
    {
        // --- Track geometry ---------------------------------------------------------------

        /// <summary>Lateral distance between lane centres, in metres.</summary>
        public float LaneWidth { get; init; } = 2.6f;

        /// <summary>
        /// Seconds to slide from one lane to the next. Tuning note: below ~0.10s the move reads
        /// as a teleport; above ~0.25s the game feels unresponsive and players blame the controls
        /// for deaths that were their own fault. 0.15 is the sweet spot most runners land on.
        /// </summary>
        public float LaneChangeDuration { get; init; } = 0.15f;

        // --- Speed ------------------------------------------------------------------------

        /// <summary>Forward speed at the start of a run, in metres/second.</summary>
        public float BaseSpeed { get; init; } = 9f;

        /// <summary>
        /// Speed added per metre travelled. Linear acceleration is intentional: exponential
        /// curves feel great for 30 seconds and then become impossible, which caps session
        /// length and therefore caps ad impressions per session.
        /// </summary>
        public float SpeedGainPerMetre { get; init; } = 0.012f;

        /// <summary>
        /// Hard ceiling on forward speed. This exists for a concrete reason: obstacles are
        /// spawned a fixed distance ahead, so beyond a certain speed the player physically
        /// cannot see and react to them within human reaction time (~250 ms). Past that point
        /// deaths stop feeling earned and start feeling cheap, and players churn.
        /// </summary>
        public float MaxSpeed { get; init; } = 26f;

        // --- Jump and slide ---------------------------------------------------------------

        /// <summary>Initial vertical velocity of a jump, in metres/second.</summary>
        public float JumpVelocity { get; init; } = 8.5f;

        /// <summary>
        /// Downward acceleration, in m/s². Deliberately far stronger than Earth gravity (9.81):
        /// a realistic arc feels floaty and sluggish in a runner. Players want to come down fast
        /// so they can act again.
        /// </summary>
        public float Gravity { get; init; } = 32f;

        /// <summary>How long a slide lasts, in seconds.</summary>
        public float SlideDuration { get; init; } = 0.55f;

        /// <summary>Collider height while sliding, as a fraction of the standing height.</summary>
        public float SlideHeightFactor { get; init; } = 0.45f;

        // --- Scoring ----------------------------------------------------------------------

        /// <summary>Score awarded per metre travelled.</summary>
        public float ScorePerMetre { get; init; } = 1f;

        /// <summary>Score awarded per coin, on top of the coin's currency value.</summary>
        public int ScorePerCoin { get; init; } = 10;

        // --- Track streaming --------------------------------------------------------------

        /// <summary>Length of one procedurally-spawned track chunk, in metres.</summary>
        public float ChunkLength { get; init; } = 30f;

        /// <summary>
        /// How many chunks are live at once. Must be enough that the furthest chunk is spawned
        /// beyond the camera's far plane, or the player watches the world pop into existence.
        /// </summary>
        public int ActiveChunks { get; init; } = 6;

        /// <summary>
        /// Metres behind the player at which a chunk is recycled back into the pool.
        /// Negative = behind the player.
        /// </summary>
        public float ChunkDespawnZ { get; init; } = -20f;

        /// <summary>
        /// Distance the player runs before obstacles begin appearing. A grace period at the
        /// start of every run measurably improves early retention: dying in the first two
        /// seconds of a fresh install reads as "this game is unfair", not "I made a mistake".
        /// </summary>
        public float SafeStartDistance { get; init; } = 40f;

        /// <summary>Validates the tuning and throws on values that would produce an unplayable run.</summary>
        public void Validate()
        {
            if (LaneWidth <= 0f) throw new ArgumentException($"{nameof(LaneWidth)} must be > 0.");
            if (LaneChangeDuration <= 0f) throw new ArgumentException($"{nameof(LaneChangeDuration)} must be > 0.");
            if (BaseSpeed <= 0f) throw new ArgumentException($"{nameof(BaseSpeed)} must be > 0.");
            if (MaxSpeed < BaseSpeed) throw new ArgumentException($"{nameof(MaxSpeed)} must be >= {nameof(BaseSpeed)}.");
            if (SpeedGainPerMetre < 0f) throw new ArgumentException($"{nameof(SpeedGainPerMetre)} must be >= 0.");
            if (Gravity <= 0f) throw new ArgumentException($"{nameof(Gravity)} must be > 0.");
            if (JumpVelocity <= 0f) throw new ArgumentException($"{nameof(JumpVelocity)} must be > 0.");
            if (SlideDuration <= 0f) throw new ArgumentException($"{nameof(SlideDuration)} must be > 0.");
            if (SlideHeightFactor is <= 0f or >= 1f) throw new ArgumentException($"{nameof(SlideHeightFactor)} must be in (0,1).");
            if (ChunkLength <= 0f) throw new ArgumentException($"{nameof(ChunkLength)} must be > 0.");
            if (ActiveChunks < 2) throw new ArgumentException($"{nameof(ActiveChunks)} must be >= 2.");
        }
    }
}
