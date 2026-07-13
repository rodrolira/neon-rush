namespace NeonRush.Domain.Run
{
    /// <summary>
    /// The three lanes of the track. The backing values are deliberately -1/0/+1 so that a lane
    /// converts to a lateral offset by a single multiply (<c>(int)lane * laneWidth</c>) with no
    /// lookup table and no branch.
    /// </summary>
    public enum Lane
    {
        Left = -1,
        Centre = 0,
        Right = 1,
    }

    /// <summary>Lane arithmetic. Pure, engine-free, and therefore trivially unit-testable.</summary>
    public static class LaneExtensions
    {
        public const int MinLane = -1;
        public const int MaxLane = 1;

        /// <summary>
        /// The lane one step to the left, or the same lane when already at the edge.
        ///
        /// Clamping rather than wrapping is a design decision, not a limitation: a swipe that
        /// teleports the player from the left lane to the right lane would read as a bug and
        /// would kill them on an obstacle they never saw coming.
        /// </summary>
        public static Lane StepLeft(this Lane lane) =>
            lane == Lane.Left ? Lane.Left : (Lane)((int)lane - 1);

        /// <summary>The lane one step to the right, or the same lane when already at the edge.</summary>
        public static Lane StepRight(this Lane lane) =>
            lane == Lane.Right ? Lane.Right : (Lane)((int)lane + 1);

        /// <summary>Lateral world offset, in metres, for this lane.</summary>
        public static float OffsetFor(this Lane lane, float laneWidth) => (int)lane * laneWidth;

        /// <summary>True when a step in <paramref name="direction"/> would actually change lane.</summary>
        public static bool CanStep(this Lane lane, int direction) =>
            direction < 0 ? lane != Lane.Left : lane != Lane.Right;
    }
}
