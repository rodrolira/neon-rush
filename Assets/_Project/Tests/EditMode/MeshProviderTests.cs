using NeonRush.Domain.PowerUps;
using NeonRush.Domain.Run;
using NeonRush.Presentation.Visuals;
using NeonRush.Presentation.World;
using NUnit.Framework;
using UnityEngine;

namespace NeonRush.Tests.EditMode
{
    /// <summary>
    /// Guards the seam between gameplay and art.
    ///
    /// These tests exist because of a specific near-miss. The collision system used to size an
    /// obstacle's hitbox from <c>transform.localScale</c>, which was correct for exactly as long as
    /// every obstacle was a unit cube stretched to its archetype's dimensions. Authored meshes are
    /// modelled at their true size and import at scale 1, so that code would have shrunk every
    /// hitbox to a 1 m cube — the player would clip through the visible edges of a 1.8 m wall and
    /// die to empty air in the middle of a low block.
    ///
    /// Nothing would have thrown. No test would have gone red. The game would simply have started
    /// lying to the player, and the only symptom would have been reviews saying the collision felt
    /// wrong. So the invariant is pinned here: <b>the hitbox comes from the archetype, and swapping
    /// the art cannot change it.</b>
    /// </summary>
    [TestFixture]
    public sealed class MeshProviderTests
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
        }

        [TearDown]
        public void TearDown()
        {
            _track?.Dispose();
            _materials.Dispose();
            Object.DestroyImmediate(_root);
        }

        /// <summary>
        /// A provider that reproduces the one property of authored art that broke the old code:
        /// meshes are already the right size, so nothing ever touches localScale.
        /// </summary>
        private sealed class RealSizeMeshProvider : IMeshProvider
        {
            private readonly PrimitiveMeshProvider _inner;

            public RealSizeMeshProvider(NeonMaterials materials) => _inner = new PrimitiveMeshProvider(materials);

            public GameObject CreateObstacle() => _inner.CreateObstacle();

            /// <summary>Deliberately does nothing: an authored mesh needs no scaling.</summary>
            public void ApplyObstacle(GameObject instance, ObstacleKind kind, ObstacleArchetype archetype) { }

            public GameObject CreateCoin(float radius) => _inner.CreateCoin(radius);
            public GameObject CreatePowerUp(float size) => _inner.CreatePowerUp(size);
            public void ApplyPowerUp(GameObject instance, PowerUpType kind) { }
            public GameObject CreatePlayer(float height) => _inner.CreatePlayer(height);

            public GameObject CreateRoadSlab(float width, float thickness, float length, Transform parent) =>
                _inner.CreateRoadSlab(width, thickness, length, parent);

            public GameObject CreateLaneMarker(float width, float thickness, float length, Transform parent) =>
                _inner.CreateLaneMarker(width, thickness, length, parent);

            public GameObject CreateBuilding(float w, float h, float d, Color trim, Transform parent) =>
                _inner.CreateBuilding(w, h, d, trim, parent);
        }

        [Test]
        public void EveryObstacleRecordsItsKind_SoTheHitboxCanBeSizedWithoutTheTransform()
        {
            _track = new TrackStreamer(_tuning, _root.transform, _materials, seed: 4242);
            _track.Reset();

            for (var frame = 0; frame < 2000; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);

                foreach (var chunk in _track.ActiveChunks)
                {
                    Assert.That(chunk.ObstacleKinds, Has.Count.EqualTo(chunk.Obstacles.Count),
                        "Obstacles and ObstacleKinds must stay parallel — if they drift, every " +
                        "obstacle past the drift point is tested against the wrong hitbox.");
                }
            }
        }

        [Test]
        public void ObstacleKindsStayParallel_AfterAShieldClearsObstaclesNearThePlayer()
        {
            // ClearObstaclesNear removes from the middle of the list. That is the one place the two
            // parallel lists can silently desynchronise, and it only runs when a shield absorbs a
            // hit — rare enough to reach production unnoticed.
            _track = new TrackStreamer(_tuning, _root.transform, _materials, seed: 99);
            _track.Reset();

            for (var frame = 0; frame < 1200; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);

                if (frame % 40 == 0) _track.ClearObstaclesNear(2.5f);

                foreach (var chunk in _track.ActiveChunks)
                {
                    Assert.That(chunk.ObstacleKinds, Has.Count.EqualTo(chunk.Obstacles.Count),
                        $"Lists drifted after a shield clear on frame {frame}.");
                }
            }
        }

        [Test]
        public void RecycledChunksDoNotAccumulateStaleKinds()
        {
            // A chunk shell is reused forever. If Recycle clears Obstacles but forgets
            // ObstacleKinds, the kinds list grows without bound and every index lines up with the
            // wrong obstacle from the second lap onward.
            _track = new TrackStreamer(_tuning, _root.transform, _materials, seed: 7);
            _track.Reset();

            for (var frame = 0; frame < 4000; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);
            }

            foreach (var chunk in _track.ActiveChunks)
            {
                Assert.That(chunk.ObstacleKinds, Has.Count.EqualTo(chunk.Obstacles.Count));
                Assert.That(chunk.ObstacleKinds.Count, Is.LessThan(64),
                    "Kinds are accumulating across recycles instead of being cleared.");
            }
        }

        [Test]
        public void AProviderThatNeverTouchesScale_StillProducesCorrectlyPlacedObstacles()
        {
            // The regression test for authored art. With this provider every obstacle stays at
            // scale 1, exactly as an imported real-size mesh would. Placement must be unaffected,
            // because it is driven by the archetype and not by the mesh.
            _track = new TrackStreamer(
                _tuning, _root.transform, _materials, seed: 31337,
                new RealSizeMeshProvider(_materials));

            _track.Reset();

            var sawAnObstacle = false;

            for (var frame = 0; frame < 3000; frame++)
            {
                _track.Tick(1f / 60f, _tuning.MaxSpeed);

                foreach (var chunk in _track.ActiveChunks)
                {
                    for (var i = 0; i < chunk.Obstacles.Count; i++)
                    {
                        var obstacle = chunk.Obstacles[i];
                        if (obstacle == null) continue;

                        sawAnObstacle = true;

                        Assert.That(obstacle.transform.localScale, Is.EqualTo(Vector3.one),
                            "This provider must leave authored meshes unscaled.");

                        var expectedY = ObstacleArchetype.For(chunk.ObstacleKinds[i]).CentreY;

                        Assert.That(obstacle.transform.localPosition.y, Is.EqualTo(expectedY).Within(0.01f),
                            "Placement must come from the archetype, not from the mesh's size.");
                    }
                }
            }

            Assert.That(sawAnObstacle, Is.True, "The test never observed an obstacle.");
        }

        [Test]
        public void TheGreyboxProviderStillStretchesItsCubes()
        {
            // The other side of the same coin: the fallback must keep working exactly as before,
            // because it is what the game runs on when no art is present.
            var provider = new PrimitiveMeshProvider(_materials);
            var cube = provider.CreateObstacle();

            try
            {
                foreach (ObstacleKind kind in System.Enum.GetValues(typeof(ObstacleKind)))
                {
                    var archetype = ObstacleArchetype.For(kind);
                    provider.ApplyObstacle(cube, kind, archetype);

                    Assert.That(cube.transform.localScale.x, Is.EqualTo(archetype.Width).Within(0.001f));
                    Assert.That(cube.transform.localScale.y, Is.EqualTo(archetype.Height).Within(0.001f));
                    Assert.That(cube.transform.localScale.z, Is.EqualTo(archetype.Depth).Within(0.001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void ThePlayerVisualHasItsOriginOnTheGround()
        {
            // Both providers must hand back a wrapper whose origin is at the feet. The bootstrap
            // relies on this to drop the half-height fudge it used to apply by hand.
            var provider = new PrimitiveMeshProvider(_materials);
            var player = provider.CreatePlayer(1.6f);

            try
            {
                Assert.That(player.transform.localPosition, Is.EqualTo(Vector3.zero));

                var renderer = player.GetComponentInChildren<MeshRenderer>();
                Assert.That(renderer, Is.Not.Null, "The player visual has no renderer.");

                Assert.That(renderer.transform.localPosition.y, Is.EqualTo(0.8f).Within(0.001f),
                    "The greybox cube is centre-origin, so it must be lifted half its height.");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }
    }
}
