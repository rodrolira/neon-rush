using NeonRush.Domain.PowerUps;
using NeonRush.Domain.Run;
using UnityEngine;
using System.Collections.Generic;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// The greybox, behind the <see cref="IMeshProvider"/> seam. Byte-for-byte the same visuals the
    /// game shipped with before authored art existed.
    ///
    /// This is not a stopgap to delete later. It is the fallback the game runs on when the art is
    /// not present — a fresh clone, a CI run that skips asset import, or a designer who wants to
    /// tune the loop without waiting for a model to be re-exported. It has no dependency on any
    /// imported asset, so it cannot break, which is exactly what a fallback needs to be.
    /// </summary>
    public sealed class PrimitiveMeshProvider : IMeshProvider
    {
        private readonly NeonMaterials _materials;
        private readonly Dictionary<PowerUpType, Material> _powerUpMaterials = new();
        private readonly Material _obstacleMaterial;
        private readonly Material _coinMaterial;
        private readonly Material _playerMaterial;
        private readonly Material _roadMaterial;
        private readonly Material _lineMaterial;
        private readonly Material _buildingMaterial;

        public PrimitiveMeshProvider(NeonMaterials materials)
        {
            _materials = materials;

            // Every material is fetched once, here. NeonMaterials caches per colour, so asking it
            // per rent would be correct but would put a dictionary lookup in the hot path for no
            // reason — and the whole point of sharing materials is that 300 obstacles batch into
            // one draw call.
            _obstacleMaterial = _materials.Get(NeonMaterials.Obstacle);
            _coinMaterial = _materials.Get(NeonMaterials.Coin, emission: 2.0f);
            _playerMaterial = _materials.Get(NeonMaterials.Player);
            _roadMaterial = _materials.Get(NeonMaterials.Road, emission: 0.05f);
            _lineMaterial = _materials.Get(NeonMaterials.LaneLine, emission: 2.2f);
            _buildingMaterial = _materials.Get(new Color(0.05f, 0.04f, 0.11f), emission: 0.0f);

            _powerUpMaterials[PowerUpType.Magnet] = _materials.Get(new Color(0.20f, 1.00f, 0.85f), emission: 2.2f);
            _powerUpMaterials[PowerUpType.Shield] = _materials.Get(new Color(0.35f, 0.60f, 1.00f), emission: 2.2f);
            _powerUpMaterials[PowerUpType.DoubleScore] = _materials.Get(new Color(1.00f, 0.82f, 0.25f), emission: 2.2f);
        }

        public GameObject CreateObstacle() =>
            PrimitiveFactory.Cube("Obstacle", Vector3.one, _obstacleMaterial);

        /// <summary>
        /// Stretches the unit cube to the archetype's dimensions. This is the behaviour the
        /// collision system used to depend on, and it is now purely cosmetic: the hitbox comes from
        /// the archetype directly (see <c>CollisionSystem.HitObstacle</c>), so this scale only
        /// decides what the player sees.
        /// </summary>
        public void ApplyObstacle(GameObject instance, ObstacleKind kind, ObstacleArchetype archetype)
        {
            instance.transform.localScale = new Vector3(archetype.Width, archetype.Height, archetype.Depth);
        }

        public GameObject CreateCoin(float radius) =>
            PrimitiveFactory.Coin("Coin", radius, _coinMaterial);

        public GameObject CreatePowerUp(float size) =>
            PrimitiveFactory.Cube("PowerUp", Vector3.one * size, _powerUpMaterials[PowerUpType.Magnet]);

        public void ApplyPowerUp(GameObject instance, PowerUpType kind)
        {
            instance.GetComponent<MeshRenderer>().sharedMaterial = _powerUpMaterials[kind];
        }

        /// <summary>
        /// The player cube, with the visual pushed up half its height inside a wrapper whose origin
        /// sits on the floor. Everything downstream — lane maths, jump height, the collision AABB —
        /// can then treat y = 0 as "feet on floor" without a half-height fudge in four places.
        /// </summary>
        public GameObject CreatePlayer(float height)
        {
            var root = new GameObject("PlayerVisual");

            var body = PrimitiveFactory.Cube(
                "PlayerBody",
                new Vector3(0.8f, height, 0.8f),
                _playerMaterial,
                root.transform);

            body.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);

            return root;
        }

        public GameObject CreateRoadSlab(float width, float thickness, float length, Transform parent) =>
            PrimitiveFactory.Cube("Road", new Vector3(width, thickness, length), _roadMaterial, parent);

        public GameObject CreateLaneMarker(float width, float thickness, float length, Transform parent) =>
            PrimitiveFactory.Cube("LaneLine", new Vector3(width, thickness, length), _lineMaterial, parent);

        public GameObject CreateBuilding(float width, float height, float depth, Color trim, Transform parent)
        {
            var root = new GameObject("Tower");

            if (parent != null)
            {
                root.transform.SetParent(parent, worldPositionStays: false);
            }

            var body = PrimitiveFactory.Cube(
                "TowerBody",
                new Vector3(width, height, depth),
                _buildingMaterial,
                root.transform);

            // The building's origin is its base, so the caller positions it on the ground with no
            // per-instance height maths. The body itself is centre-origin, hence the half-height.
            body.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);

            var strip = PrimitiveFactory.Cube(
                "Trim",
                new Vector3(width + 0.15f, 0.15f, depth + 0.15f),
                _materials.Get(trim, emission: 2.4f),
                root.transform);

            strip.transform.localPosition = new Vector3(0f, height + 0.08f, 0f);

            return root;
        }
    }
}
