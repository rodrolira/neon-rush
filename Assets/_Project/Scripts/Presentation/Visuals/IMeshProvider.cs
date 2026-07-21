using NeonRush.Domain.PowerUps;
using NeonRush.Domain.Run;
using UnityEngine;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// The seam between "what the game spawns" and "what it looks like".
    ///
    /// Until now the answer to both was <see cref="PrimitiveFactory"/>: the track streamer and the
    /// bootstrap called it directly, so swapping in authored art meant editing gameplay code. This
    /// interface is the one place that knowledge now lives. <see cref="PrimitiveMeshProvider"/>
    /// reproduces the greybox exactly, and <see cref="CatalogMeshProvider"/> serves authored
    /// meshes; nothing else in the project can tell which one it is talking to.
    ///
    /// <b>The split into Create/Apply is what makes pooling survive the change.</b> Objects are
    /// pooled and one instance is reused across kinds — the same pooled obstacle is a low block in
    /// one chunk and a wall in the next. So creation (which may allocate, and only ever happens
    /// during pre-warm) is separated from per-rent configuration (which must not allocate, because
    /// it runs mid-run and this project forbids a GC spike during a run).
    ///
    /// Implementations must therefore guarantee: <c>Create*</c> may allocate freely, <c>Apply*</c>
    /// allocates nothing and only toggles or assigns what is already there.
    /// </summary>
    public interface IMeshProvider
    {
        /// <summary>
        /// Creates one pooled obstacle instance, able to represent any <see cref="ObstacleKind"/>.
        /// Called only during pool pre-warm.
        /// </summary>
        GameObject CreateObstacle();

        /// <summary>
        /// Configures a pooled instance to read as <paramref name="kind"/>. Called on every rent,
        /// so it must not allocate.
        /// </summary>
        void ApplyObstacle(GameObject instance, ObstacleKind kind, ObstacleArchetype archetype);

        /// <summary>Creates one pooled coin of the given radius, in metres.</summary>
        GameObject CreateCoin(float radius);

        /// <summary>Creates one pooled pickup instance, able to represent any power-up kind.</summary>
        GameObject CreatePowerUp(float size);

        /// <summary>Configures a pooled pickup to read as <paramref name="kind"/>. Must not allocate.</summary>
        void ApplyPowerUp(GameObject instance, PowerUpType kind);

        /// <summary>
        /// Creates the player's visual, with its origin at the feet so y = 0 means "on the floor".
        /// </summary>
        GameObject CreatePlayer(float height);

        /// <summary>Creates one road slab spanning a full chunk.</summary>
        GameObject CreateRoadSlab(float width, float thickness, float length, Transform parent);

        /// <summary>Creates one lane divider spanning a full chunk.</summary>
        GameObject CreateLaneMarker(float width, float thickness, float length, Transform parent);

        /// <summary>
        /// Creates one skyline building of the given size, with its neon crown in
        /// <paramref name="trim"/>. The returned object's origin is its base, on the ground.
        /// </summary>
        GameObject CreateBuilding(float width, float height, float depth, Color trim, Transform parent);
    }
}
