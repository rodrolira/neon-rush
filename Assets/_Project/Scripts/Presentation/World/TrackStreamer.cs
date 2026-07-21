using System;
using System.Collections.Generic;
using NeonRush.Domain.PowerUps;
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

        /// <summary>
        /// The kind of each obstacle, parallel with <see cref="Obstacles"/>.
        ///
        /// This exists so the collision system can size a hitbox from the archetype rather than
        /// from the obstacle's <c>transform.localScale</c>. Reading the scale worked only while
        /// every obstacle was a unit cube stretched to size; the moment a real mesh authored at its
        /// true dimensions arrives, its scale is 1 and the hitbox silently collapses to a 1 m cube.
        /// Deriving the hitbox from the same struct that defines the archetype makes the art and
        /// the collision volume impossible to desynchronise — you cannot change one without the
        /// other, because there is only one of them.
        /// </summary>
        public readonly List<ObstacleKind> ObstacleKinds = new();

        public readonly List<GameObject> Coins = new();

        /// <summary>Coins already banked this pass, so a coin cannot be collected twice.</summary>
        public readonly List<bool> CoinTaken = new();

        /// <summary>Power-up pickups carried by this chunk, parallel with <see cref="PowerUpKinds"/> and <see cref="PowerUpTaken"/>.</summary>
        public readonly List<GameObject> PowerUps = new();
        public readonly List<PowerUpType> PowerUpKinds = new();

        /// <summary>Pickups already grabbed this pass, so one cannot be collected twice.</summary>
        public readonly List<bool> PowerUpTaken = new();

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
        private readonly System.Random _random;

        /// <summary>Plans each row's obstacle layout. Pure and tested — see <see cref="RowPlanner"/>.</summary>
        private readonly RowPlanner _rowPlanner;

        /// <summary>Reused per-row buffer so planning a row allocates nothing mid-run.</summary>
        private readonly ObstacleKind?[] _rowBuffer = new ObstacleKind?[LaneCount];

        private readonly GameObjectPool _chunkPool;
        private readonly GameObjectPool _obstaclePool;
        private readonly GameObjectPool _coinPool;
        private readonly GameObjectPool _powerUpPool;

        /// <summary>
        /// Decides what everything looks like. The streamer knows what to spawn and where; it has
        /// no opinion on whether that is a stretched cube or an authored mesh.
        /// </summary>
        private readonly IMeshProvider _meshes;

        // --- Power-up spawning and magnet state -------------------------------------------------
        // Both are configured from outside (GameBootstrap) after construction, so the streamer's
        // constructor stays free of the power-up config and the existing track tests keep passing
        // with power-ups simply switched off.

        /// <summary>Probability a chunk carries a pickup. 0 = power-ups disabled (the default).</summary>
        private float _powerUpChancePerChunk;

        private bool _magnetActive;
        private float _magnetPlayerX;
        private float _magnetRadius;
        private float _magnetPullSpeed;

        private readonly List<Chunk> _active = new();

        /// <summary>Chunk shells not currently in play. Reused so no allocation happens mid-run.</summary>
        private readonly Stack<Chunk> _chunkShells = new();

        /// <summary>Z at which the next chunk will be emitted.</summary>
        private float _nextChunkZ;

        /// <summary>Total distance the world has scrolled. Drives difficulty and the safe-start window.</summary>
        private float _distance;

        private bool _disposed;

        /// <param name="meshes">
        /// What everything looks like. Optional: omitted, the streamer builds the greybox provider
        /// itself, so every existing test and any code that predates authored art keeps working
        /// with no change at the call site.
        /// </param>
        public TrackStreamer(
            RunTuning tuning,
            Transform worldRoot,
            NeonMaterials materials,
            int seed,
            IMeshProvider meshes = null)
        {
            _tuning = tuning;
            _worldRoot = worldRoot;

            // The materials are no longer held: everything that needed one now goes through the
            // provider. The parameter stays so the default greybox provider can be built here, and
            // so every existing call site keeps compiling unchanged.
            _meshes = meshes ?? new PrimitiveMeshProvider(materials);

            // Seeded, so a run is reproducible from its seed. That is what makes "the player says
            // they died to an impossible spawn at 1,240 m" a bug we can actually replay, and it is
            // also the foundation for server-side run validation later.
            _random = new System.Random(seed);

            _rowPlanner = new RowPlanner(LaneCount);

            _chunkPool = new GameObjectPool(
                BuildChunkVisual,
                _worldRoot,
                prewarm: _tuning.ActiveChunks + 2);

            // Pre-warm generously. Every instance we fail to pre-warm here is an Instantiate during
            // a run, which is a GC spike, which is a dropped frame at the worst possible moment.
            // Memory is cheap; a stutter mid-run is not.
            //
            // One pool serves every obstacle kind, and the provider decides how a single instance
            // manages to be all three: the greybox stretches a unit cube, the catalog toggles which
            // authored mesh is visible. A shared pool means a low block recycled behind the player
            // can come back as a wall ahead of it, so the kind mix costs no extra memory.
            _obstaclePool = new GameObjectPool(
                _meshes.CreateObstacle,
                _worldRoot,
                prewarm: _tuning.ActiveChunks * RowsPerChunk * 2);

            _coinPool = new GameObjectPool(
                () => _meshes.CreateCoin(CoinRadius),
                _worldRoot,
                prewarm: _tuning.ActiveChunks * RowsPerChunk * LaneCount);

            // At most one pickup per chunk, so a couple of pooled instances cover every live chunk.
            _powerUpPool = new GameObjectPool(
                () => _meshes.CreatePowerUp(PowerUpSize),
                _worldRoot,
                prewarm: _tuning.ActiveChunks + 2);
        }

        private const float CoinRadius = 0.35f;

        /// <summary>Edge length of a power-up pickup cube, in metres.</summary>
        private const float PowerUpSize = 0.7f;

        /// <summary>Height at which coins float, in metres. Reachable while running; never requires a jump.</summary>
        private const float CoinHeight = 0.9f;

        /// <summary>Height at which pickups float. Reachable on the ground; grabbing one never needs a jump.</summary>
        private const float PowerUpHeight = 1.0f;

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
            _magnetActive = false;

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
            AttractCoins(deltaTime);
        }

        /// <summary>Degrees per second a coin rotates about the vertical axis.</summary>
        private const float CoinSpinSpeed = 180f;

        /// <summary>Amplitude of the coin's vertical bob, in metres. Subtle on purpose.</summary>
        private const float CoinBobAmplitude = 0.08f;

        /// <summary>Elapsed streamer time, drives the bob phase.</summary>
        private float _time;

        /// <summary>
        /// Spins and bobs the coins.
        ///
        /// This is not decoration. A static gold disc reads as scenery; a spinning, gently floating
        /// one reads as *collectable*, and the player's eye tracks it without being told to. It is
        /// the cheapest possible way to teach the player what to run toward.
        ///
        /// The bob phase is offset by world X so a row of coins shimmers as a wave rather than
        /// pumping in lockstep — lockstep motion reads as mechanical, waves read as alive.
        ///
        /// Cost is a rotation and a sine on the ~90 coins currently in the world — trivial, and it
        /// happens inside the loop the streamer already runs, so it costs no extra iteration.
        /// </summary>
        private void SpinCoins(float deltaTime)
        {
            _time += deltaTime;

            var degrees = CoinSpinSpeed * deltaTime;

            for (var c = 0; c < _active.Count; c++)
            {
                var chunk = _active[c];
                var coins = chunk.Coins;

                for (var i = 0; i < coins.Count; i++)
                {
                    var coin = coins[i];
                    if (coin == null) continue; // Already collected this pass.

                    coin.transform.Rotate(Vector3.up, degrees, Space.World);

                    var position = coin.transform.localPosition;
                    position.y = CoinHeight + Mathf.Sin(_time * 3f + position.x * 1.7f + position.z * 0.3f) * CoinBobAmplitude;
                    coin.transform.localPosition = position;
                }

                // Pickups get the same tumble so they read as collectable, at a distinct rate so a
                // pickup never looks like just a big coin.
                var pickups = chunk.PowerUps;
                for (var i = 0; i < pickups.Count; i++)
                {
                    var pickup = pickups[i];
                    if (pickup == null) continue;

                    pickup.transform.Rotate(new Vector3(0.4f, 1f, 0.2f), degrees * 0.6f, Space.Self);
                }
            }
        }

        /// <summary>
        /// While the magnet is active, reels every coin inside its radius toward the player. Collection
        /// itself still happens in the collision system when the coin reaches the player's box — the
        /// magnet only shortens the distance, it does not bank the coin, so there is one authority on
        /// what counts as "collected".
        ///
        /// Runs after <see cref="SpinCoins"/>, which sets each coin's bob height; pulling toward the
        /// player afterwards simply overrides that for the coins being reeled in, which is what we want.
        /// </summary>
        private void AttractCoins(float deltaTime)
        {
            if (!_magnetActive) return;

            var step = _magnetPullSpeed * deltaTime;
            var radiusSq = _magnetRadius * _magnetRadius;

            // The player stands at world z = 0; the pull target is their lane at coin height.
            var target = new Vector3(_magnetPlayerX, CoinHeight, 0f);

            for (var c = 0; c < _active.Count; c++)
            {
                var chunk = _active[c];
                var coins = chunk.Coins;

                for (var i = 0; i < coins.Count; i++)
                {
                    var coin = coins[i];
                    if (coin == null) continue;

                    // World position, since the target is world-space and chunks are offset in Z.
                    var world = coin.transform.position;
                    var toTarget = target - world;

                    if (toTarget.sqrMagnitude > radiusSq) continue;

                    coin.transform.position = Vector3.MoveTowards(world, target, step);
                }
            }
        }

        /// <summary>
        /// Removes every obstacle within <paramref name="metres"/> of the player, in both directions.
        ///
        /// Called on revive. Without it, the player is resurrected standing inside the obstacle that
        /// just killed them, the collision test fires on the very next frame, and they die again
        /// instantly — having just paid for the privilege with a 30-second ad. That is not a bug the
        /// player forgives; it is the kind that produces a refund request and a one-star review, and
        /// it is entirely avoidable with four lines of code.
        ///
        /// The window is generous on purpose. Clearing only the exact obstacle they touched still
        /// leaves them a fraction of a second from the next one, at 26 m/s, with no time to react.
        /// A revive must hand back a survivable situation, not a technically-alive one.
        /// </summary>
        public void ClearObstaclesNear(float metres)
        {
            for (var c = 0; c < _active.Count; c++)
            {
                var chunk = _active[c];
                var obstacles = chunk.Obstacles;

                for (var i = obstacles.Count - 1; i >= 0; i--)
                {
                    var obstacle = obstacles[i];
                    if (obstacle == null) continue;

                    if (Mathf.Abs(obstacle.transform.position.z) > metres) continue;

                    _obstaclePool.Return(obstacle);
                    obstacles.RemoveAt(i);

                    // Kept in lockstep. If these two lists ever drift, every obstacle after the
                    // removal point is tested against the wrong archetype's hitbox — a bug that
                    // only shows up after a shield absorbs a hit, which is rare enough to reach
                    // production. Iterating backwards is what makes the paired RemoveAt safe.
                    chunk.ObstacleKinds.RemoveAt(i);
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

        /// <summary>Marks a pickup as taken and returns it to the pool.</summary>
        internal void TakePowerUp(Chunk chunk, int index)
        {
            chunk.PowerUpTaken[index] = true;

            var pickup = chunk.PowerUps[index];
            if (pickup != null)
            {
                _powerUpPool.Return(pickup);
                chunk.PowerUps[index] = null;
            }
        }

        /// <summary>
        /// Turns power-up spawning on and sets how often a chunk carries one. Called once by the
        /// composition root after reading the config; left off (chance 0) if power-ups are disabled,
        /// which is also the default, so any test that does not opt in sees a track with no pickups.
        /// </summary>
        public void EnablePowerUps(float chancePerChunk)
        {
            _powerUpChancePerChunk = Mathf.Clamp01(chancePerChunk);
        }

        /// <summary>
        /// Feeds the magnet its state for the coming frame: whether it is active and where the player
        /// is, so <see cref="Tick"/> can reel coins in. Called every frame before <see cref="Tick"/>.
        /// Passing <paramref name="active"/> false (the default state) leaves coins untouched.
        /// </summary>
        public void SetMagnet(bool active, float playerX, float radius, float pullSpeed)
        {
            _magnetActive = active;
            _magnetPlayerX = playerX;
            _magnetRadius = radius;
            _magnetPullSpeed = pullSpeed;
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

            for (var i = 0; i < chunk.PowerUps.Count; i++)
            {
                if (chunk.PowerUps[i] != null) _powerUpPool.Return(chunk.PowerUps[i]);
            }

            chunk.Obstacles.Clear();
            chunk.ObstacleKinds.Clear();
            chunk.Coins.Clear();
            chunk.CoinTaken.Clear();
            chunk.PowerUps.Clear();
            chunk.PowerUpKinds.Clear();
            chunk.PowerUpTaken.Clear();

            _chunkPool.Return(chunk.Root);
            chunk.Root = null;

            _chunkShells.Push(chunk);
        }

        /// <summary>
        /// Fills a chunk with obstacles and coins.
        ///
        /// The row layout — which lanes are blocked, by which kind of obstacle, and the guarantee
        /// that one lane is always left empty — is decided by the pure, tested <see cref="RowPlanner"/>.
        /// This method's only job is to turn that plan into pooled GameObjects at the right size and
        /// height. Keeping the survivability logic out of here is deliberate: a rule that can kill the
        /// player unfairly belongs in Domain where it is unit-tested, not tangled up with Instantiate
        /// calls that need a running scene to exercise.
        /// </summary>
        private void PopulateChunk(Chunk chunk)
        {
            // Difficulty ramps with distance, then plateaus. Beyond the plateau the game gets faster
            // (speed keeps climbing) but not denser — density and speed compounding together becomes
            // unfair rather than hard.
            var difficulty = Mathf.Clamp01(_distance / 2000f);

            var rowSpacing = _tuning.ChunkLength / RowsPerChunk;

            // Decide up front whether this chunk carries a pickup, and in which row. The lane is
            // chosen per-row from whatever ends up empty, so a pickup never lands inside an obstacle.
            var spawnPickup = _powerUpChancePerChunk > 0f && _random.NextDouble() < _powerUpChancePerChunk;
            var pickupRow = spawnPickup ? _random.Next(RowsPerChunk) : -1;

            for (var row = 0; row < RowsPerChunk; row++)
            {
                var localZ = row * rowSpacing + rowSpacing * 0.5f;
                var worldZ = chunk.Z + localZ;

                // Grace period at the start of a run: coins only, no obstacles.
                var safe = worldZ < _tuning.SafeStartDistance;

                _rowPlanner.Plan(_rowBuffer, difficulty, safe, _random);

                var pickupLane = row == pickupRow ? PickEmptyLane(_rowBuffer) : -1;

                for (var laneIndex = 0; laneIndex < LaneCount; laneIndex++)
                {
                    var lane = (Lane)(laneIndex - 1); // 0,1,2 -> -1,0,1
                    var x = lane.OffsetFor(_tuning.LaneWidth);

                    var kind = _rowBuffer[laneIndex];

                    if (kind.HasValue)
                    {
                        SpawnObstacle(chunk, kind.Value, x, localZ);
                    }
                    else if (laneIndex == pickupLane)
                    {
                        SpawnPowerUp(chunk, ChoosePowerUpKind(), x, localZ);
                    }
                    else
                    {
                        // Coins go where it is safe to run. That is not decoration — it is how the
                        // game teaches the correct line without a tutorial. The player learns to
                        // follow the gold, and the gold is always in a lane that is survivable by
                        // simply staying in it.
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
        /// Picks a random empty lane from a planned row, or -1 if the row is somehow full (it never is
        /// — the planner guarantees an empty lane — but the caller handles -1 rather than assuming).
        /// </summary>
        private int PickEmptyLane(ObstacleKind?[] row)
        {
            // Reservoir pick so every empty lane is equally likely without allocating a list.
            var chosen = -1;
            var seen = 0;

            for (var i = 0; i < row.Length; i++)
            {
                if (row[i].HasValue) continue;

                seen++;
                if (_random.Next(seen) == 0) chosen = i;
            }

            return chosen;
        }

        /// <summary>Weighted pick of a pickup kind. The shield is the rarest because it is the strongest.</summary>
        private PowerUpType ChoosePowerUpKind()
        {
            var roll = _random.NextDouble();

            if (roll < 0.45) return PowerUpType.Magnet;
            if (roll < 0.80) return PowerUpType.DoubleScore;
            return PowerUpType.Shield;
        }

        private void SpawnPowerUp(Chunk chunk, PowerUpType kind, float x, float localZ)
        {
            var pickup = _powerUpPool.Rent();
            pickup.transform.SetParent(chunk.Root.transform, worldPositionStays: false);
            pickup.transform.localPosition = new Vector3(x, PowerUpHeight, localZ);

            // Sizing happens once, at creation, not here. Re-applying a 0.7 scale on every rent was
            // harmless for a unit cube but would shrink an authored mesh — which is already 0.7 m
            // across — a second time on every single spawn.
            _meshes.ApplyPowerUp(pickup, kind);

            chunk.PowerUps.Add(pickup);
            chunk.PowerUpKinds.Add(kind);
            chunk.PowerUpTaken.Add(false);
        }

        /// <summary>
        /// Rents a pooled obstacle and shapes it into the given kind, raised to the archetype's
        /// centre height so a hanging slide-under barrier floats and a grounded block sits on the
        /// floor.
        ///
        /// How the instance is made to *look* like that kind is the mesh provider's business: the
        /// greybox provider stretches a unit cube, while a provider backed by authored art swaps
        /// which child mesh is visible and leaves the scale at 1. Either way the collision hitbox
        /// is unaffected, because it is derived from <see cref="ObstacleArchetype"/> via
        /// <see cref="Chunk.ObstacleKinds"/> and not from the transform.
        /// </summary>
        private void SpawnObstacle(Chunk chunk, ObstacleKind kind, float x, float localZ)
        {
            var archetype = ObstacleArchetype.For(kind);

            var obstacle = _obstaclePool.Rent();
            obstacle.transform.SetParent(chunk.Root.transform, worldPositionStays: false);
            obstacle.transform.localPosition = new Vector3(x, archetype.CentreY, localZ);

            _meshes.ApplyObstacle(obstacle, kind, archetype);

            chunk.Obstacles.Add(obstacle);
            chunk.ObstacleKinds.Add(kind);
        }

        /// <summary>Builds the visual shell of a chunk: road, lane markers, and the flanking skyline.</summary>
        private GameObject BuildChunkVisual()
        {
            var root = new GameObject("Chunk");

            var width = _tuning.LaneWidth * LaneCount + 1.2f;

            var slab = _meshes.CreateRoadSlab(width, 0.2f, _tuning.ChunkLength, root.transform);

            // The slab is authored around its centre, but the chunk's origin is its near edge, so
            // push it half a chunk forward and drop it just below y=0 (the plane the player runs on).
            slab.transform.localPosition = new Vector3(0f, -0.1f, _tuning.ChunkLength * 0.5f);

            for (var i = 0; i < 2; i++)
            {
                var x = (i == 0 ? -0.5f : 0.5f) * _tuning.LaneWidth;

                var marker = _meshes.CreateLaneMarker(0.08f, 0.02f, _tuning.ChunkLength, root.transform);

                marker.transform.localPosition = new Vector3(x, 0.005f, _tuning.ChunkLength * 0.5f);
            }

            BuildSkyline(root.transform);

            return root;
        }

        /// <summary>Neon accent palette for building trims. Cycled deterministically per building.</summary>
        private static readonly Color[] TrimColours =
        {
            new(0.20f, 1.00f, 0.85f), // cyan
            new(1.00f, 0.25f, 0.75f), // magenta
            new(0.55f, 0.35f, 1.00f), // violet
            new(1.00f, 0.82f, 0.25f), // gold (rare warm accent)
        };

        /// <summary>
        /// The flanking skyline: dark towers with emissive neon roof trims on both sides of the road.
        ///
        /// This is what turns "a road in a void" into "a road through a city", and it costs almost
        /// nothing: the buildings are part of the pooled chunk shell, built once when the shell is
        /// created and recycled with it forever after. With ~8 pooled shells the skyline pattern
        /// repeats every ~8 chunks — at 26 m/s, through fog, with randomised heights per shell,
        /// nobody can perceive the period.
        ///
        /// Buildings sit OUTSIDE the outer lanes and never collide with anything — they are set
        /// dressing, invisible to the AABB system, which only ever tests chunk.Obstacles.
        /// </summary>
        private void BuildSkyline(Transform root)
        {
            const int buildingsPerSide = 4;
            var edge = _tuning.LaneWidth * LaneCount * 0.5f + 1.2f;
            var spacing = _tuning.ChunkLength / buildingsPerSide;

            for (var side = -1; side <= 1; side += 2)
            {
                for (var i = 0; i < buildingsPerSide; i++)
                {
                    // Deterministic variety from the seeded random: every pooled shell rolls its own
                    // skyline once, at pool-warm time, and keeps it. No per-frame cost, ever.
                    var height = 4f + (float)_random.NextDouble() * 12f;
                    var buildingWidth = 2.5f + (float)_random.NextDouble() * 2.5f;
                    var depth = 2.5f + (float)_random.NextDouble() * 2f;
                    var gap = 1.5f + (float)_random.NextDouble() * 3f;

                    var x = side * (edge + gap + buildingWidth * 0.5f);
                    var z = i * spacing + spacing * 0.5f;

                    // The neon roof trim: a thin, hot strip capping the tower. This is the detail
                    // the bloom pass turns into the skyline's glow. The provider owns whether that
                    // is a separate cube or already baked into an authored block's crown.
                    var trim = TrimColours[_random.Next(TrimColours.Length)];

                    var building = _meshes.CreateBuilding(buildingWidth, height, depth, trim, root);

                    // A building's origin is its base, so it goes straight down on the ground with
                    // no half-height maths — and, more usefully, the caller no longer has to know
                    // how tall the thing it just asked for actually turned out to be.
                    building.transform.localPosition = new Vector3(x, 0f, z);

                    // Rotating in 90-degree steps is how the skyline gets variety from a small set
                    // of authored blocks. It is free for the greybox cube and it is the only
                    // permitted variation for authored art, where a non-uniform scale would stretch
                    // the facade texture and break the crown's proportions.
                    building.transform.localRotation = Quaternion.Euler(0f, 90f * _random.Next(4), 0f);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _chunkPool.Dispose();
            _obstaclePool.Dispose();
            _coinPool.Dispose();
            _powerUpPool.Dispose();

            _active.Clear();
            _chunkShells.Clear();
        }
    }
}
