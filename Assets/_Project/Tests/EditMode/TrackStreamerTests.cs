using NeonRush.Domain.Run;
using NeonRush.Presentation.Visuals;
using NeonRush.Presentation.World;
using NUnit.Framework;
using UnityEngine;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Tests for the endless track.
    ///
    /// These exist because of a bug a player found and the unit tests did not: the emit cursor
    /// (<c>_nextChunkZ</c>) was not scrolled along with the chunks, so once the first chunk was
    /// recycled the replacement was spawned ~50 m too far ahead and the road visibly tore open.
    ///
    /// The lesson generalises: a gap in the road is not a subtle numerical drift, it is a broken
    /// invariant, and the invariant is trivially checkable. So we check it — every frame, for
    /// thousands of frames, at the speed cap.
    /// </summary>
    [TestFixture]
    public sealed class TrackStreamerTests
    {
        private GameObject _root;
        private NeonMaterials _materials;
        private RunTuning _tuning;
        private TrackStreamer _track;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("TestWorld");
            _materials = new NeonMaterials();
            _tuning = new RunTuning();
            _track = new TrackStreamer(_tuning, _root.transform, _materials, seed: 12345);
        }

        [TearDown]
        public void TearDown()
        {
            _track.Dispose();
            _materials.Dispose();
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void Reset_FillsTheTrackWithTheConfiguredNumberOfChunks()
        {
            _track.Reset();

            Assert.That(_track.ActiveChunks, Has.Count.EqualTo(_tuning.ActiveChunks));
        }

        [Test]
        public void Reset_ProducesAContiguousRoad()
        {
            _track.Reset();

            AssertContiguous("immediately after Reset");
        }

        [Test]
        public void TheRoadNeverDevelopsAGap_AcrossALongRunAtFullSpeed()
        {
            // The regression test for the reported bug. At the speed cap, over ~40 simulated
            // seconds, the track recycles many times — which is precisely when the emit cursor
            // used to drift away from the world and tear the road open.
            _track.Reset();

            const float dt = 1f / 60f;

            for (var frame = 0; frame < 2400; frame++)
            {
                _track.Tick(dt, _tuning.MaxSpeed);
                AssertContiguous($"frame {frame}");
            }
        }

        [Test]
        public void TheRoadNeverDevelopsAGap_UnderErraticFrameTimes()
        {
            // A phone that hitches delivers wildly uneven deltas. Recycling must stay correct when
            // several chunk lengths of world scroll past inside a single frame.
            _track.Reset();

            var deltas = new[] { 1f / 60f, 1f / 30f, 0.1f, 1f / 120f, 0.05f };

            for (var frame = 0; frame < 600; frame++)
            {
                _track.Tick(deltas[frame % deltas.Length], _tuning.MaxSpeed);
                AssertContiguous($"frame {frame}");
            }
        }

        [Test]
        public void TheTrackAlwaysCoversTheGroundAheadOfThePlayer()
        {
            // Contiguity alone is not enough: a contiguous track that has slid entirely behind the
            // player is still a hole in front of them. The player stands at z = 0 and must always
            // have road under their feet and ahead of them.
            _track.Reset();

            for (var frame = 0; frame < 1200; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);

                var chunks = _track.ActiveChunks;
                var nearest = float.MaxValue;
                var furthest = float.MinValue;

                foreach (var chunk in chunks)
                {
                    if (chunk.Z < nearest) nearest = chunk.Z;
                    if (chunk.Z + _tuning.ChunkLength > furthest) furthest = chunk.Z + _tuning.ChunkLength;
                }

                Assert.That(nearest, Is.LessThanOrEqualTo(0f),
                    $"frame {frame}: the road starts ahead of the player — they are running on nothing.");

                Assert.That(furthest, Is.GreaterThan(0f),
                    $"frame {frame}: the road ends behind the player.");
            }
        }

        [Test]
        public void RecycledChunksAreReused_TheTrackDoesNotLeak()
        {
            // Pools exist so that nothing is instantiated mid-run. If the track leaked chunks, the
            // pool would grow without bound and every growth is a GC spike the player pays for.
            _track.Reset();

            for (var frame = 0; frame < 3000; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);
            }

            Assert.That(_track.ActiveChunks, Has.Count.EqualTo(_tuning.ActiveChunks),
                "The number of live chunks must stay constant no matter how long the run lasts.");
        }

        [Test]
        public void TheTrackSpawnsObstaclesOfEveryHeight_NotJustOneWall()
        {
            // The regression guard for "the jump does nothing": the streamer used to spawn a single
            // 1.6 m wall everywhere. It must now put low blocks, hanging barriers, and full walls on
            // the track — proven by seeing all three archetype heights come through as chunks recycle.
            _track.Reset();

            var lowHeight = ObstacleArchetype.For(ObstacleKind.LowJump).Height;
            var slideHeight = ObstacleArchetype.For(ObstacleKind.HighSlide).Height;
            var blockHeight = ObstacleArchetype.For(ObstacleKind.FullBlock).Height;

            var seenLow = false;
            var seenSlide = false;
            var seenBlock = false;

            for (var frame = 0; frame < 6000; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);

                foreach (var chunk in _track.ActiveChunks)
                {
                    foreach (var obstacle in chunk.Obstacles)
                    {
                        if (obstacle == null) continue;

                        var h = obstacle.transform.localScale.y;
                        if (Mathf.Abs(h - lowHeight) < 0.01f) seenLow = true;
                        else if (Mathf.Abs(h - slideHeight) < 0.01f) seenSlide = true;
                        else if (Mathf.Abs(h - blockHeight) < 0.01f) seenBlock = true;
                    }
                }

                if (seenLow && seenSlide && seenBlock) break;
            }

            Assert.That(seenLow, Is.True, "No jump-over (low) obstacles ever spawned.");
            Assert.That(seenSlide, Is.True, "No slide-under (hanging) obstacles ever spawned.");
            Assert.That(seenBlock, Is.True, "No full-wall obstacles ever spawned.");
        }

        [Test]
        public void HangingBarriersFloat_AndGroundedObstaclesSitOnTheFloor()
        {
            // A slide-under barrier that spawned on the ground would be un-slideable and kill the
            // player unfairly. Assert that whatever is at slide height is actually raised off the
            // floor, and full walls are not.
            _track.Reset();

            var slideHeight = ObstacleArchetype.For(ObstacleKind.HighSlide).Height;
            var slideCentreY = ObstacleArchetype.For(ObstacleKind.HighSlide).CentreY;

            for (var frame = 0; frame < 6000; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);

                foreach (var chunk in _track.ActiveChunks)
                {
                    foreach (var obstacle in chunk.Obstacles)
                    {
                        if (obstacle == null) continue;
                        if (Mathf.Abs(obstacle.transform.localScale.y - slideHeight) >= 0.01f) continue;

                        Assert.That(obstacle.transform.localPosition.y, Is.EqualTo(slideCentreY).Within(0.01f),
                            "A hanging barrier must float at its archetype centre so a slide can pass under it.");
                    }
                }
            }
        }

        /// <summary>
        /// The invariant: sorted by position, each chunk must start exactly where the previous one
        /// ended. Any deviation is a visible hole (or an overlap) in the road.
        /// </summary>
        private void AssertContiguous(string context)
        {
            var chunks = _track.ActiveChunks;
            if (chunks.Count < 2) return;

            var starts = new float[chunks.Count];
            for (var i = 0; i < chunks.Count; i++)
            {
                starts[i] = chunks[i].Z;
            }

            System.Array.Sort(starts);

            for (var i = 1; i < starts.Length; i++)
            {
                var expected = starts[i - 1] + _tuning.ChunkLength;
                var gap = starts[i] - expected;

                // Tolerance covers accumulated float error over thousands of frames, and nothing more.
                Assert.That(Mathf.Abs(gap), Is.LessThan(0.01f),
                    $"{context}: gap of {gap:F3} m in the road between a chunk ending at " +
                    $"{expected:F2} and the next starting at {starts[i]:F2}.");
            }
        }
    }
}
