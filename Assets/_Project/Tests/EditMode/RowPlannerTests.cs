using System;
using System.Collections.Generic;
using NeonRush.Domain.Run;
using NUnit.Framework;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the row planner — the pure logic that decides what stands in each lane.
    ///
    /// The headline invariant is survivability: no matter the difficulty or the seed, every row must
    /// leave the player at least one lane they can survive by simply staying in it. A single row that
    /// breaks this is an unavoidable death, and an unavoidable death is the fastest uninstall in the
    /// genre. So we assert it directly, across tens of thousands of rows.
    /// </summary>
    [TestFixture]
    public sealed class RowPlannerTests
    {
        private const int LaneCount = 3;

        private RowPlanner _planner;
        private ObstacleKind?[] _row;

        [SetUp]
        public void SetUp()
        {
            _planner = new RowPlanner(LaneCount);
            _row = new ObstacleKind?[LaneCount];
        }

        [Test]
        public void EveryRow_LeavesAtLeastOneLaneEmpty_AcrossAllDifficulties()
        {
            var random = new Random(12345);

            for (var i = 0; i < 50_000; i++)
            {
                var difficulty = (i % 101) / 100f; // sweep 0.00 .. 1.00 repeatedly

                _planner.Plan(_row, difficulty, safe: false, random);

                Assert.That(CountEmpty(_row), Is.GreaterThanOrEqualTo(1),
                    $"row {i} at difficulty {difficulty:F2} blocked every lane — an unavoidable death.");
            }
        }

        [Test]
        public void SafeRows_AreAlwaysCompletelyEmpty()
        {
            var random = new Random(999);

            for (var i = 0; i < 1000; i++)
            {
                _planner.Plan(_row, difficulty: 1f, safe: true, random);

                Assert.That(CountEmpty(_row), Is.EqualTo(LaneCount),
                    "The safe-start window must contain no obstacles at all.");
            }
        }

        [Test]
        public void Planning_IsDeterministic_ForTheSameSeed()
        {
            var a = Plans(new Random(2024), rows: 500);
            var b = Plans(new Random(2024), rows: 500);

            CollectionAssert.AreEqual(a, b,
                "Same seed must produce the same track — replay and server validation depend on it.");
        }

        [Test]
        public void AllThreeKinds_AppearOverAThoroughRun()
        {
            var random = new Random(7);
            var seen = new HashSet<ObstacleKind>();

            for (var i = 0; i < 5000; i++)
            {
                _planner.Plan(_row, difficulty: 0.6f, safe: false, random);

                foreach (var cell in _row)
                {
                    if (cell.HasValue) seen.Add(cell.Value);
                }
            }

            Assert.That(seen, Does.Contain(ObstacleKind.LowJump));
            Assert.That(seen, Does.Contain(ObstacleKind.HighSlide));
            Assert.That(seen, Does.Contain(ObstacleKind.FullBlock));
        }

        [Test]
        public void SlideBarriers_GrowMoreCommonWithDifficulty()
        {
            // The design intent: overhead barriers — the hardest read — are rare early and common
            // late. If a retune inverts that, this catches it.
            var slidesEarly = CountKind(ObstacleKind.HighSlide, difficulty: 0.0f, seed: 42, rows: 20_000);
            var slidesLate = CountKind(ObstacleKind.HighSlide, difficulty: 1.0f, seed: 42, rows: 20_000);

            Assert.That(slidesLate, Is.GreaterThan(slidesEarly),
                "Slide-under barriers should become more frequent as difficulty rises.");
        }

        [Test]
        public void Constructor_RejectsTooFewLanes()
        {
            Assert.That(() => new RowPlanner(1), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Plan_RejectsAWrongSizedBuffer()
        {
            var wrong = new ObstacleKind?[LaneCount + 1];
            Assert.That(() => _planner.Plan(wrong, 0.5f, false, new Random(1)),
                Throws.TypeOf<ArgumentException>());
        }

        // --- helpers ------------------------------------------------------------------------------

        private static int CountEmpty(ObstacleKind?[] row)
        {
            var empty = 0;
            foreach (var cell in row)
            {
                if (!cell.HasValue) empty++;
            }

            return empty;
        }

        private List<string> Plans(Random random, int rows)
        {
            var result = new List<string>(rows);
            var buffer = new ObstacleKind?[LaneCount];

            for (var i = 0; i < rows; i++)
            {
                var difficulty = (i % 101) / 100f;
                _planner.Plan(buffer, difficulty, safe: false, random);
                result.Add(string.Join(",", Array.ConvertAll(buffer, c => c?.ToString() ?? "_")));
            }

            return result;
        }

        private int CountKind(ObstacleKind kind, float difficulty, int seed, int rows)
        {
            var random = new Random(seed);
            var buffer = new ObstacleKind?[LaneCount];
            var count = 0;

            for (var i = 0; i < rows; i++)
            {
                _planner.Plan(buffer, difficulty, safe: false, random);

                foreach (var cell in buffer)
                {
                    if (cell == kind) count++;
                }
            }

            return count;
        }
    }
}
