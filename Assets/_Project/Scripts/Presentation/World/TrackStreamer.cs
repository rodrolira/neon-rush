using System;
using System.Collections.Generic;
using NeonRush.Domain.Run;
using NeonRush.Presentation.Pooling;
using NeonRush.Presentation.Visuals;
using UnityEngine;

namespace NeonRush.Presentation.World
{
    /// <summary>One live piece of track, with whatever it is carrying.</summary>
    internal sealed class Chunk
    {
        public GameObject Root;
        public readonly List<GameObject> Obstacles = new();
        public readonly List<GameObject> Coins = new();

        /// <summary>Coins already banked this pass, so a coin cannot be collected twice.</summary>
        public readonly List<bool> CoinTaken = new();

        public float Z;
    }

    /// <summary>
    /// Generates the endless track: spawns chunks ahead, scrolls them toward the player, recycles
    /// them behind, and populates each one with obstacles and coins.
    ///
    /// The world moves; the player does not. Every chunk's Z decreases by <c>speed * dt</c> each
    /// frame, and once a chunk passes the despawn line it is returned to the pool and re-emitted at
    /// the far end. Consequences worth stating plainly:
    ///
    ///  · Distance is unbounded but coordinates are not. Nothing in the scene ever exceeds a few
    ///    hundred units from the origin, so float precision is constant from metre 1 to metre
    ///    20,000. A conventional forward-moving player would be losing sub-millimetre precision by
    ///    then, and the jitter shows up first in the camera and the collision tests.
    ///  · Recycling is a single float compare per chunk, not a distance check per object.
    ///
    /// Layout generation is intentionally simple and readable: it is a first-pass difficulty model
    /// meant to be replaced by data (Remote Config / Addressable chunk prefabs). What it must get
    /// right today is the one thing that cannot be fixed later by tuning — it must never generate
    /// an <b>unsurvivable</b> row. See <see cref="PopulateChunk"/>.
    /// </summary>
    public sealed class TrackStreamer : IDisposable
    {
        private const int LaneCount = 3;

        /// <summary>Rows of content per chunk. 30 m chunk / 5 rows = one decision every 6 m.</summary>
        private const int RowsPerChunk = 5;

        private readonly RunTuning _tuning;
        private readonly Transform _worldRoot;
        private readonly NeonMaterials _materials;
        private readonly System.Random _random;

        private readonly GameObjectPool _chunkPool;
        private readonly GameObjectPool _obstaclePool;
        private readonly GameObjectPool _coinPool;

        private readonly List<Chunk> _active = new();

        /// <summary>Chunk shells not currently in play. Reused so no allocation happens mid-run.</summary>
        private readonly Stack<Chunk> _chunkShells = new();

        /// <summary>Z at which the next chunk will be emitted.</summary>
        private float _nextChunkZ;

        /// <summary>Total distance the world has scrolled. Drives difficulty and the safe-start window.</summary>
        private float _distance;

        private bool _disposed;

        public TrackStreamer(RunTuning tuning, Transform worldRoot, NeonMaterials materials, int seed)
        {
            _tuning = tuning;
            _worldRoot = worldRoot;
            _materials = materials;

            // Seeded, so a run is reproducible from its seed. That is what makes "the player says
            // they died to an impossible spawn at 1,240 m" a bug we can actually replay, and it is
            // also the foundation for server-side run validation later.
            _random = new System.Random(seed);

            var roadMaterial = _materials.Get(NeonMaterials.Road, emission: 0.05f);
            var lineMaterial = _materials.Get(NeonMaterials.LaneLine, emission: 2.2f);
            var obstacleMaterial = _materials.Get(NeonMaterials.Obstacle);
            var coinMaterial = _materials.Get(NeonMaterials.Coin, emission: 2.0f);

            _chunkPool = new GameObjectPool(
                () => BuildChunkVisual(roadMaterial, lineMaterial),
                _worldRoot,
                prewarm: _tuning.ActiveChunks + 2);

            // Pre-warm generously. Every instance we fail to pre-warm here is an Instantiate during
            // a run, which is a GC spike, which is a dropped frame at the worst possible moment.
            // Memory is cheap; a stutter mid-run is not.
            _obstaclePool = new GameObjectPool(
                () => PrimitiveFactory.Cube("Obstacle", ObstacleSize, obstacleMaterial),
                _worldRoot,
                prewarm: _tuning.ActiveChunks * RowsPerChunk * 2);

            _coinPool = new GameObjectPool(
                () => PrimitiveFactory.Coin("Coin", CoinRadius, coinMaterial),
                _worldRoot,
                prewarm: _tuning.ActiveChunks * RowsPerChunk * LaneCount);
        }

        private static readonly Vector3 ObstacleSize = new(1.8f, 1.6f, 1.2f);
        private const float CoinRadius = 0.35f;

        /// <summary>Height at which coins float, in metres. Reachable while running; never requires a jump.</summary>
        private const float CoinHeight = 0.9f;

        /// <summary>True once the pools have been forced to grow mid-run — a pre-warm bug worth surfacing.</summary>
        public bool PoolsGrewUnderLoad =>
            _chunkPool.GrewUnderLoad || _obstaclePool.GrewUnderLoad || _coinPool.GrewUnderLoad;

        /// <summary>Every obstacle currently in the world, in world space. Read by the collision system.</summary>
        internal IReadOnlyList<Chunk> ActiveChunks => _active;

        /// <summary>Clears the track and rebuilds the starting chunks. Called at the start of every run.</summary>
        public void Reset()
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                Recycle(_active[i]);
            }

            _active.Clear();
            _distance = 0f;
            _nextChunkZ = 0f;

            for (var i = 0; i < _tuning.ActiveChunks; i++)
            {
                Emit();
            }
        }

        /// <summary>Scrolls the world toward the player and recycles anything that has passed them.</summary>
        public void Tick(float deltaTime, float speed)
        {
            var delta = speed * deltaTime;
            _distance += delta;

            // The emit cursor lives in the same moving world as the chunks, so it must scroll with
            // them. Leaving it fixed while the chunks slide backwards was a real bug: by the time
            // the first chunk was recycled the world had moved ~50 m under a stationary cursor, so
            // the replacement chunk was emitted 50 m too far ahead and tore a hole in the road.
            _nextChunkZ -= delta;

            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var chunk = _active[i];
                chunk.Z -= delta;

                var position = chunk.Root.transform.localPosition;
                position.z = chunk.Z;
                chunk.Root.transform.localPosition = position;

                if (chunk.Z + _tuning.ChunkLength < _tuning.ChunkDespawnZ)
                {
                    Recycle(chunk);
                    _active.RemoveAt(i);
                }
            }

            // Top the track back up. A while-loop rather than an if: a single very long frame (an
            // app resume, a GC pause) can scroll past more than one chunk length, and an if would
            // leave a visible hole in the world.
            while (_active.Count < _tuning.ActiveChunks)
            {
                Emit();
            }

            SpinCoins(deltaTime);
        }

        /// <summary>Degrees per second a coin rotates about the vertical axis.</summary>
        private const float CoinSpinSpeed = 180f;

        /// <summary>
        /// Spins the coins.
        ///
        /// This is not decoration. A static gold disc reads as scenery; a spinning one reads as
        /// *collectable*, and the player's eye tracks it without being told to. It is the cheapest
        /// possible way to teach the player what to run toward.
        ///
        /// Cost is a rotation on the ~90 coins currently in the world — trivial, and it happens
        /// inside the loop the streamer already runs, so it costs no extra iteration.
        /// </summary>
        private void SpinCoins(float deltaTime)
        {
            var degrees = CoinSpinSpeed * deltaTime;

            for (var c = 0; c < _active.Count; c++)
            {
                var coins = _active[c].Coins;

                for (var i = 0; i < coins.Count; i++)
                {
                    var coin = coins[i];
                    if (coin == null) continue; // Already collected this pass.

                    coin.transform.Rotate(Vector3.up, degrees, Space.World);
                }
            }
        }

        /// <summary>Marks a coin as taken and returns it to the pool.</summary>
        internal void TakeCoin(Chunk chunk, int index)
        {
            chunk.CoinTaken[index] = true;

            var coin = chunk.Coins[index];
            if (coin != null)
            {
                _coinPool.Return(coin);
                chunk.Coins[index] = null;
            }
        }

        private void Emit()
        {
            var chunk = _chunkShells.Count > 0 ? _chunkShells.Pop() : new Chunk();

            chunk.Root = _chunkPool.Rent();
            chunk.Z = _nextChunkZ;
            chunk.Root.transform.localPosition = new Vector3(0f, 0f, chunk.Z);

            PopulateChunk(chunk);

            _active.Add(chunk);
            _nextChunkZ += _tuning.ChunkLength;
        }

        private void Recycle(Chunk chunk)
        {
            for (var i = 0; i < chunk.Obstacles.Count; i++)
            {
                if (chunk.Obstacles[i] != null) _obstaclePool.Return(chunk.Obstacles[i]);
            }

            for (var i = 0; i < chunk.Coins.Count; i++)
            {
                if (chunk.Coins[i] != null) _coinPool.Return(chunk.Coins[i]);
            }

            chunk.Obstacles.Clear();
            chunk.Coins.Clear();
            chunk.CoinTaken.Clear();

            _chunkPool.Return(chunk.Root);
            chunk.Root = null;

            _chunkShells.Push(chunk);
        }

        /// <summary>
        /// Fills a chunk with obstacles and coins.
        ///
        /// The invariant that must never be violated: <b>at least one lane in every row is
        /// passable.</b> If all three lanes are blocked, the player dies to a situation with no
        /// legal solution — and a death the player could not have avoided is the fastest way to
        /// make them uninstall. Difficulty here comes from *how many* lanes are blocked and how
        /// little time there is to react, never from making a row unsurvivable.
        /// </summary>
        private void PopulateChunk(Chunk chunk)
        {
            // Difficulty ramps with distance, then plateaus. Beyond the plateau the game gets faster
            // (speed keeps climbing) but not denser — density and speed compounding together becomes
            // unfair rather than hard.
            var difficulty = Mathf.Clamp01(_distance / 2000f);

            var rowSpacing = _tuning.ChunkLength / RowsPerChunk;

            for (var row = 0; row < RowsPerChunk; row++)
            {
                var localZ = row * rowSpacing + rowSpacing * 0.5f;
                var worldZ = chunk.Z + localZ;

                // Grace period at the start of a run: coins only, no obstacles. Dying in the first
                // two seconds of a fresh install reads as "this game is unfair", not "I made a
                // mistake", and it is measurable in D1 retention.
                var safe = worldZ < _tuning.SafeStartDistance;

                var blockedLanes = safe ? 0 : ChooseBlockedLaneCount(difficulty);

                // Pick which lanes are blocked, guaranteeing at least one free lane.
                var free = _random.Next(LaneCount); // the lane we promise to leave open

                for (var laneIndex = 0; laneIndex < LaneCount; laneIndex++)
                {
                    var lane = (Lane)(laneIndex - 1); // 0,1,2 -> -1,0,1
                    var x = lane.OffsetFor(_tuning.LaneWidth);

                    var isBlocked = !safe
                                    && laneIndex != free
                                    && blockedLanes > 0
                                    && ShouldBlock(laneIndex, free, blockedLanes);

                    if (isBlocked)
                    {
                        var obstacle = _obstaclePool.Rent();
                        obstacle.transform.SetParent(chunk.Root.transform, worldPositionStays: false);
                        obstacle.transform.localPosition = new Vector3(x, ObstacleSize.y * 0.5f, localZ);

                        chunk.Obstacles.Add(obstacle);
                    }
                    else
                    {
                        // Coins go where it is safe to run. That is not decoration — it is how the
                        // game teaches the correct line without a tutorial. The player learns to
                        // follow the gold, and the gold is always survivable.
                        if (_random.NextDouble() < 0.55)
                        {
                            var coin = _coinPool.Rent();
                            coin.transform.SetParent(chunk.Root.transform, worldPositionStays: false);
                            coin.transform.localPosition = new Vector3(x, CoinHeight, localZ);

                            chunk.Coins.Add(coin);
                            chunk.CoinTaken.Add(false);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// How many lanes to block in a row, as difficulty rises. Never returns 3 — that is the
        /// unsurvivable case, and it is excluded structurally rather than by a comment asking
        /// future maintainers to be careful.
        /// </summary>
        private int ChooseBlockedLaneCount(float difficulty)
        {
            var roll = _random.NextDouble();

            // At difficulty 0: mostly empty rows. At difficulty 1: usually one or two lanes blocked.
            var emptyChance = Mathf.Lerp(0.65f, 0.20f, difficulty);
            var doubleChance = Mathf.Lerp(0.05f, 0.35f, difficulty);

            if (roll < emptyChance) return 0;
            if (roll > 1.0 - doubleChance) return 2;

            return 1;
        }

        /// <summary>Deterministically decides whether a given lane is one of the blocked ones.</summary>
        private static bool ShouldBlock(int laneIndex, int freeLane, int blockedLanes)
        {
            if (laneIndex == freeLane) return false;

            if (blockedLanes >= 2) return true; // both non-free lanes

            // Exactly one blocked lane: pick the first non-free lane deterministically so the row
            // is stable if it is ever regenerated from the same seed.
            var firstNonFree = freeLane == 0 ? 1 : 0;
            return laneIndex == firstNonFree;
        }

        /// <summary>Builds the visual shell of a chunk: road slab plus two lane markers.</summary>
        private GameObject BuildChunkVisual(Material road, Material line)
        {
            var root = new GameObject("Chunk");

            var width = _tuning.LaneWidth * LaneCount + 1.2f;

            var slab = PrimitiveFactory.Cube(
                "Road",
                new Vector3(width, 0.2f, _tuning.ChunkLength),
                road,
                root.transform);

            // The slab is authored around its centre, but the chunk's origin is its near edge, so
            // push it half a chunk forward and drop it just below y=0 (the plane the player runs on).
            slab.transform.localPosition = new Vector3(0f, -0.1f, _tuning.ChunkLength * 0.5f);

            for (var i = 0; i < 2; i++)
            {
                var x = (i == 0 ? -0.5f : 0.5f) * _tuning.LaneWidth;

                var marker = PrimitiveFactory.Cube(
                    "LaneLine",
                    new Vector3(0.08f, 0.02f, _tuning.ChunkLength),
                    line,
                    root.transform);

                marker.transform.localPosition = new Vector3(x, 0.005f, _tuning.ChunkLength * 0.5f);
            }

            return root;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _chunkPool.Dispose();
            _obstaclePool.Dispose();
            _coinPool.Dispose();

            _active.Clear();
            _chunkShells.Clear();
        }
    }
}
