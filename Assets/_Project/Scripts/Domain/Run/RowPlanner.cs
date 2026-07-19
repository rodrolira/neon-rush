using System;

namespace NeonRush.Domain.Run
{
    /// <summary>
    /// Decides the contents of a single row of the track: which lanes are blocked, and by which kind
    /// of obstacle.
    ///
    /// Pure and deterministic. Given the same difficulty and the same <see cref="Random"/> sequence
    /// it produces the same row, which is what lets a run be replayed from its seed — for bug reports
    /// today and server-side score validation later. It is also fully unit-testable without a scene,
    /// which matters because the one rule it must never break is not a visual nicety:
    ///
    ///  <b>Every row leaves at least one lane completely empty.</b>
    ///
    /// That empty lane is the guaranteed survival path: the player can always reach it and simply do
    /// nothing. The jump and the slide are how the player passes the *other* lanes — for a better
    /// line, or a coin — never the only thing standing between them and an unavoidable death.
    /// Difficulty comes from how many lanes are blocked and how little time there is to read them,
    /// never from a row with no legal answer. That invariant is asserted directly in the tests.
    ///
    /// This lives in Domain (no engine reference) on purpose: the survivability guarantee is logic,
    /// not rendering, and logic that can kill the player unfairly deserves to be tested like logic.
    /// </summary>
    public sealed class RowPlanner
    {
        private readonly int _laneCount;

        public RowPlanner(int laneCount)
        {
            if (laneCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(laneCount), "Need at least two lanes to guarantee an escape lane.");
            }

            _laneCount = laneCount;
        }

        /// <summary>
        /// Plans one row into the caller's buffer. Each slot is null (empty lane) or the kind of
        /// obstacle in that lane. The buffer is caller-owned and reused across rows so no allocation
        /// happens per row mid-run — the same discipline the object pools follow.
        /// </summary>
        /// <param name="lanes">Buffer of length <c>laneCount</c>, overwritten in full.</param>
        /// <param name="difficulty">0 at the start of a run, 1 at the difficulty plateau.</param>
        /// <param name="safe">True inside the safe-start window: the row is left empty of obstacles.</param>
        /// <param name="random">Deterministic source, owned by the caller so the whole run stays reproducible.</param>
        public void Plan(ObstacleKind?[] lanes, float difficulty, bool safe, Random random)
        {
            if (lanes == null) throw new ArgumentNullException(nameof(lanes));
            if (lanes.Length != _laneCount)
                throw new ArgumentException($"Expected a buffer of {_laneCount} lanes.", nameof(lanes));
            if (random == null) throw new ArgumentNullException(nameof(random));

            for (var i = 0; i < lanes.Length; i++) lanes[i] = null;

            // Grace period at the start of a run: no obstacles at all. Dying in the first two seconds
            // of a fresh install reads as "this game is unfair", and it shows up in D1 retention.
            if (safe) return;

            var blockedLanes = ChooseBlockedLaneCount(difficulty, random);
            if (blockedLanes == 0) return;

            // The lane we promise to leave empty — the guaranteed do-nothing survival path.
            var free = random.Next(_laneCount);

            for (var lane = 0; lane < _laneCount; lane++)
            {
                if (lane == free) continue;
                if (!ShouldBlock(lane, free, blockedLanes)) continue;

                lanes[lane] = ChooseKind(difficulty, random);
            }
        }

        /// <summary>
        /// How many lanes to block in a row, as difficulty rises. Never returns the lane count — an
        /// all-blocked row is the unsurvivable case, and it is excluded structurally rather than by a
        /// comment asking a future maintainer to be careful.
        /// </summary>
        private int ChooseBlockedLaneCount(float difficulty, Random random)
        {
            var roll = random.NextDouble();

            // At difficulty 0: mostly empty rows. At difficulty 1: usually one or two lanes blocked.
            var emptyChance = Lerp(0.65f, 0.20f, difficulty);
            var doubleChance = Lerp(0.05f, 0.35f, difficulty);

            if (roll < emptyChance) return 0;
            if (roll > 1.0 - doubleChance) return Math.Min(2, _laneCount - 1);
            return 1;
        }

        /// <summary>
        /// Picks an obstacle kind, with the mix shifting as difficulty rises.
        ///
        /// Early on the track is mostly jumpable low blocks and full walls — jump and dodge, the two
        /// verbs a new player learns first. The slide-under barrier starts rare and grows more common
        /// with difficulty, because reading an overhead barrier and committing to a slide at speed is
        /// the hardest of the three reactions.
        /// </summary>
        private static ObstacleKind ChooseKind(float difficulty, Random random)
        {
            var roll = random.NextDouble();

            var slideChance = Lerp(0.15f, 0.35f, difficulty);
            var lowChance = Lerp(0.45f, 0.30f, difficulty);

            if (roll < slideChance) return ObstacleKind.HighSlide;
            if (roll < slideChance + lowChance) return ObstacleKind.LowJump;
            return ObstacleKind.FullBlock;
        }

        /// <summary>Deterministically decides whether a given lane is one of the blocked ones.</summary>
        private static bool ShouldBlock(int laneIndex, int freeLane, int blockedLanes)
        {
            if (laneIndex == freeLane) return false;
            if (blockedLanes >= 2) return true; // every non-free lane

            // Exactly one blocked lane: pick the first non-free lane deterministically so the row is
            // stable if it is ever regenerated from the same seed.
            var firstNonFree = freeLane == 0 ? 1 : 0;
            return laneIndex == firstNonFree;
        }

        private static float Lerp(float a, float b, float t)
        {
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return a + (b - a) * t;
        }
    }
}
