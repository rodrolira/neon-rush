using NeonRush.Application.Events;
using NeonRush.Application.PowerUps;
using NeonRush.Application.Run;
using NeonRush.Presentation.Player;
using UnityEngine;

namespace NeonRush.Presentation.World
{
    /// <summary>
    /// Collision, done by hand. Neon Rush uses no Unity colliders and no Rigidbodies anywhere.
    ///
    /// That is not contrarianism; the physics engine is the wrong tool for this specific game, for
    /// three concrete reasons:
    ///
    ///  1. <b>Tunnelling.</b> At the 30 m/s speed cap, an obstacle moves ~0.50 m per frame at 60 fps
    ///     and ~1.0 m at 30 fps. Obstacles are 1.2 m deep. On a phone that drops to 20 fps for a
    ///     moment, a discrete collider test can miss the overlap entirely and the player passes
    ///     straight through an obstacle. Unity's answer is continuous collision detection, which is
    ///     substantially more expensive — for a problem we do not need to have.
    ///  2. <b>Everything moves.</b> The world scrolls, so every collider in the scene is a moving
    ///     collider. Unity rebuilds its broadphase for those every frame. We would be paying for a
    ///     spatial index over a few hundred boxes whose positions we already know exactly.
    ///  3. <b>Determinism.</b> A hand-rolled AABB test gives the same answer on every device, every
    ///     time. That is what lets a run be replayed from its seed for bug reports and, later, for
    ///     server-side validation of a submitted score.
    ///
    /// The cost of this test is a handful of float compares against only the objects inside a
    /// narrow Z window around the player. It does not scale with the size of the world.
    /// </summary>
    public sealed class CollisionSystem
    {
        /// <summary>
        /// Half-depth of the window around the player that we bother testing, in metres.
        /// Generously larger than the fastest per-frame step (~1.0 m at 30 fps, and 3.0 m at the
        /// clamped worst case) plus the deepest object, so nothing can cross the window untested:
        /// skipping the full 6 m window in one frame would take 60 m/s, twice the speed cap.
        /// </summary>
        private const float TestWindow = 3.0f;

        /// <summary>
        /// Metres of obstacles cleared around the player when a shield absorbs a hit. Without this the
        /// player would be resurrected still inside the obstacle they just hit and die on the very next
        /// frame — the same trap <see cref="TrackStreamer.ClearObstaclesNear"/> guards against on a
        /// revive. Smaller than the revive window because a shield leaves the run flowing, not paused.
        /// </summary>
        private const float ShieldClearMetres = 2.5f;

        private readonly TrackStreamer _track;
        private readonly PlayerMotor _player;
        private readonly RunSession _session;
        private readonly float _chunkLength;

        /// <summary>Optional. When present, an obstacle hit is offered to the shield before it is fatal.</summary>
        private readonly PowerUpService _powerUps;

        public CollisionSystem(
            TrackStreamer track,
            PlayerMotor player,
            RunSession session,
            float chunkLength,
            PowerUpService powerUps = null)
        {
            _track = track;
            _player = player;
            _session = session;
            _chunkLength = chunkLength;
            _powerUps = powerUps;
        }

        /// <summary>
        /// Tests the player against everything nearby. Collects coins, and ends the run on the
        /// first obstacle hit.
        /// </summary>
        public void Tick()
        {
            if (!_session.IsRunning) return;

            var playerBounds = _player.Bounds;

            foreach (var chunk in _track.ActiveChunks)
            {
                if (IsOutsideTestWindow(chunk)) continue;

                CollectCoins(chunk, playerBounds);
                CollectPowerUps(chunk, playerBounds);

                if (HitObstacle(chunk, playerBounds))
                {
                    // A shield, if the player has one, turns a fatal hit into a survivable one: it is
                    // spent, the obstacles around the player are cleared so they do not immediately
                    // re-collide, and the run flows on. No shield, no reprieve.
                    if (_powerUps != null && _powerUps.TryConsumeShield())
                    {
                        _track.ClearObstaclesNear(ShieldClearMetres);
                        return;
                    }

                    _session.End(DeathCause.HitObstacle);
                    return;
                }
            }
        }

        /// <summary>
        /// Rejects a whole chunk before touching any of its contents.
        ///
        /// A chunk spans [Z, Z + chunkLength] and the player sits at z = 0. If that span cannot
        /// reach the test window, none of the chunk's children can either, so the entire chunk is
        /// skipped with two float compares instead of iterating its obstacles and coins.
        /// </summary>
        private bool IsOutsideTestWindow(Chunk chunk)
        {
            var near = chunk.Z;
            var far = chunk.Z + _chunkLength;

            // Entirely ahead of the window, or entirely behind it.
            return near > TestWindow || far < -TestWindow;
        }

        private void CollectCoins(Chunk chunk, Bounds playerBounds)
        {
            for (var i = 0; i < chunk.Coins.Count; i++)
            {
                if (chunk.CoinTaken[i]) continue;

                var coin = chunk.Coins[i];
                if (coin == null) continue;

                var position = coin.transform.position;

                if (Mathf.Abs(position.z) > TestWindow) continue;

                // Coins use a slightly generous box. A coin that requires pixel-perfect alignment
                // feels stingy and the player reads near-misses as the game cheating them; a
                // forgiving pickup radius feels good and costs nothing.
                var coinBounds = new Bounds(position, new Vector3(0.9f, 0.9f, 0.7f));

                if (!coinBounds.Intersects(playerBounds)) continue;

                _track.TakeCoin(chunk, i);
                _session.CollectCoin();
            }
        }

        private void CollectPowerUps(Chunk chunk, Bounds playerBounds)
        {
            if (_powerUps == null) return;

            for (var i = 0; i < chunk.PowerUps.Count; i++)
            {
                if (chunk.PowerUpTaken[i]) continue;

                var pickup = chunk.PowerUps[i];
                if (pickup == null) continue;

                var position = pickup.transform.position;

                if (Mathf.Abs(position.z) > TestWindow) continue;

                // The same forgiving box as coins: a pickup that demanded pixel-perfect alignment
                // would feel stingy, and a missed power-up stings more than a missed coin.
                var pickupBounds = new Bounds(position, new Vector3(0.9f, 0.9f, 0.7f));

                if (!pickupBounds.Intersects(playerBounds)) continue;

                _powerUps.Collect(chunk.PowerUpKinds[i]);
                _track.TakePowerUp(chunk, i);
            }
        }

        private static bool HitObstacle(Chunk chunk, Bounds playerBounds)
        {
            for (var i = 0; i < chunk.Obstacles.Count; i++)
            {
                var obstacle = chunk.Obstacles[i];
                if (obstacle == null) continue;

                var position = obstacle.transform.position;

                if (Mathf.Abs(position.z) > TestWindow) continue;

                // Obstacle hitboxes are shrunk ~15% relative to the mesh. This is deliberate and it
                // is one of the highest-leverage feel decisions in the whole game: a hitbox that
                // exactly matches the art produces deaths the player is certain they should have
                // survived. Players do not perceive a slightly generous hitbox as easy — they
                // perceive an exact one as unfair. Err toward the player, always.
                var scale = obstacle.transform.localScale;
                var hitbox = new Bounds(position, new Vector3(scale.x * 0.85f, scale.y * 0.9f, scale.z * 0.85f));

                if (hitbox.Intersects(playerBounds)) return true;
            }

            return false;
        }
    }
}
