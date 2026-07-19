namespace NeonRush.Domain.Run
{
    /// <summary>
    /// The kinds of obstacle, distinguished by the single action that clears each one.
    ///
    /// This distinction is the whole reason to have more than one obstacle. A track built from one
    /// kind only ever asks the player to do one thing, and every other move — the jump, the slide —
    /// becomes dead weight the player never uses. That was the bug this enum exists to fix: every
    /// obstacle was a full-height wall taller than the jump, so the jump button did nothing and the
    /// only verb in the game was "change lane".
    ///
    /// The geometry that makes each kind solvable by exactly one move (and no other) lives in
    /// <see cref="ObstacleArchetype"/>, tuned against the player's jump and slide in <see cref="RunTuning"/>.
    /// </summary>
    public enum ObstacleKind
    {
        /// <summary>A low block on the ground. Cleared by jumping; a slide hugs the floor and still hits it.</summary>
        LowJump = 0,

        /// <summary>An overhead barrier with a gap beneath. Cleared by sliding; a jump flies straight into it.</summary>
        HighSlide = 1,

        /// <summary>A full-height wall: too tall to jump, too low to slide under. The only answer is to change lane.</summary>
        FullBlock = 2,
    }
}
