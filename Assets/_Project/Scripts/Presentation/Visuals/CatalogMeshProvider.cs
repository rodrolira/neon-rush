using System.Collections.Generic;
using NeonRush.Domain.PowerUps;
using NeonRush.Domain.Run;
using UnityEngine;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// Serves authored meshes from a <see cref="ModelCatalog"/>, falling back to the greybox for
    /// anything the catalog does not have.
    ///
    /// <b>How one pooled object represents three obstacle kinds.</b> The pool is deliberately shared
    /// across kinds — a low block recycled behind the player comes back as a wall ahead of it, which
    /// is what keeps the kind mix free of extra memory. With a stretched cube that was trivial. With
    /// three distinct meshes it is not, so a pooled instance is a container holding all three
    /// meshes as children, and <see cref="ApplyObstacle"/> activates exactly one.
    ///
    /// The cost is three MeshRenderers per pooled instance where there was one, of which two are
    /// always disabled. That is a few hundred bytes each and no GPU work at all, and it buys the
    /// thing that actually matters: <c>SetActive</c> on a rent, instead of <c>Instantiate</c>.
    /// Instantiating the right prefab per rent would allocate during a run, which this project
    /// forbids outright — a GC spike mid-run is a dropped frame at the exact moment the player is
    /// reacting to an obstacle, and they read that as an unfair death.
    /// </summary>
    public sealed class CatalogMeshProvider : IMeshProvider
    {
        private readonly ModelCatalog _catalog;
        private readonly PrimitiveMeshProvider _fallback;

        /// <summary>
        /// Lookup from a pooled container to its per-kind children. Keyed by the container itself:
        /// these are pool instances that live for the whole session and are torn down together with
        /// this provider, so there is no destroyed-object-leak to avoid — and keying by reference
        /// sidesteps Unity 6's deprecation of the integer instance id. Populated only at pre-warm;
        /// nothing is added to it during a run.
        /// </summary>
        private readonly Dictionary<GameObject, GameObject[]> _obstacleParts = new();
        private readonly Dictionary<GameObject, GameObject[]> _powerUpParts = new();

        private static readonly ObstacleKind[] ObstacleKinds =
        {
            ObstacleKind.LowJump, ObstacleKind.HighSlide, ObstacleKind.FullBlock,
        };

        private static readonly PowerUpType[] PowerUpKinds =
        {
            PowerUpType.Magnet, PowerUpType.Shield, PowerUpType.DoubleScore,
        };

        public CatalogMeshProvider(ModelCatalog catalog, PrimitiveMeshProvider fallback)
        {
            _catalog = catalog;
            _fallback = fallback;
        }

        // --- Obstacles ----------------------------------------------------------------------

        public GameObject CreateObstacle()
        {
            var prefabs = new[]
            {
                _catalog.ObstacleLowJump, _catalog.ObstacleHighSlide, _catalog.ObstacleFullBlock,
            };

            // If any kind is missing art, the whole obstacle path stays on the greybox. Mixing an
            // authored wall with a magenta cube would look like a bug rather than like work in
            // progress, and the cube's stretched scale would be the only thing keeping it the right
            // size — a rule that no longer holds for its authored siblings.
            if (prefabs[0] == null || prefabs[1] == null || prefabs[2] == null)
            {
                return _fallback.CreateObstacle();
            }

            var container = new GameObject("Obstacle");
            var parts = new GameObject[prefabs.Length];

            for (var i = 0; i < prefabs.Length; i++)
            {
                parts[i] = Object.Instantiate(prefabs[i], container.transform);
                parts[i].name = ObstacleKinds[i].ToString();
                parts[i].transform.localPosition = Vector3.zero;
                parts[i].transform.localRotation = Quaternion.identity;
                StripCollidersAndShadows(parts[i]);
                parts[i].SetActive(false);
            }

            _obstacleParts[container] = parts;
            return container;
        }

        public void ApplyObstacle(GameObject instance, ObstacleKind kind, ObstacleArchetype archetype)
        {
            if (!_obstacleParts.TryGetValue(instance, out var parts))
            {
                // This instance came from the fallback, so it is a unit cube that still needs
                // stretching to the archetype.
                _fallback.ApplyObstacle(instance, kind, archetype);
                return;
            }

            // Authored meshes are modelled at their true size, so the container stays at scale 1.
            // Scaling here would double-apply the archetype's dimensions and produce a 3.24 m wall.
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i].SetActive(ObstacleKinds[i] == kind);
            }
        }

        // --- Coin ---------------------------------------------------------------------------

        public GameObject CreateCoin(float radius)
        {
            if (_catalog.Coin == null) return _fallback.CreateCoin(radius);

            var coin = Object.Instantiate(_catalog.Coin);
            coin.name = "Coin";
            StripCollidersAndShadows(coin);
            return coin;
        }

        // --- Power-ups ----------------------------------------------------------------------

        public GameObject CreatePowerUp(float size)
        {
            var prefabs = new[]
            {
                _catalog.PowerUpMagnet, _catalog.PowerUpShield, _catalog.PowerUpDoubleScore,
            };

            if (prefabs[0] == null || prefabs[1] == null || prefabs[2] == null)
            {
                return _fallback.CreatePowerUp(size);
            }

            var container = new GameObject("PowerUp");
            var parts = new GameObject[prefabs.Length];

            for (var i = 0; i < prefabs.Length; i++)
            {
                parts[i] = Object.Instantiate(prefabs[i], container.transform);
                parts[i].name = PowerUpKinds[i].ToString();
                parts[i].transform.localPosition = Vector3.zero;
                parts[i].transform.localRotation = Quaternion.identity;
                StripCollidersAndShadows(parts[i]);
                parts[i].SetActive(false);
            }

            _powerUpParts[container] = parts;
            return container;
        }

        public void ApplyPowerUp(GameObject instance, PowerUpType kind)
        {
            if (!_powerUpParts.TryGetValue(instance, out var parts))
            {
                _fallback.ApplyPowerUp(instance, kind);
                return;
            }

            for (var i = 0; i < parts.Length; i++)
            {
                parts[i].SetActive(PowerUpKinds[i] == kind);
            }
        }

        // --- Player -------------------------------------------------------------------------

        public GameObject CreatePlayer(float height)
        {
            if (_catalog.PlayerDefault == null) return _fallback.CreatePlayer(height);

            var root = new GameObject("PlayerVisual");
            var body = Object.Instantiate(_catalog.PlayerDefault, root.transform);
            body.name = "PlayerBody";
            body.transform.localPosition = Vector3.zero;

            // The authored character's origin is already between its feet, so unlike the greybox
            // cube there is no half-height offset to apply. The wrapper is kept anyway so both
            // providers hand back the same shape of object and the caller needs no special case.
            StripCollidersAndShadows(body);
            return root;
        }

        // --- Track and environment ------------------------------------------------------------

        public GameObject CreateRoadSlab(float width, float thickness, float length, Transform parent)
        {
            if (_catalog.RoadSlab == null) return _fallback.CreateRoadSlab(width, thickness, length, parent);

            var slab = Object.Instantiate(_catalog.RoadSlab, parent);
            slab.name = "Road";
            StripCollidersAndShadows(slab);
            return slab;
        }

        public GameObject CreateLaneMarker(float width, float thickness, float length, Transform parent)
        {
            if (_catalog.LaneMarker == null) return _fallback.CreateLaneMarker(width, thickness, length, parent);

            var marker = Object.Instantiate(_catalog.LaneMarker, parent);
            marker.name = "LaneLine";
            StripCollidersAndShadows(marker);
            return marker;
        }

        public GameObject CreateBuilding(float width, float height, float depth, Color trim, Transform parent)
        {
            if (_catalog.Buildings == null || _catalog.Buildings.Length == 0)
            {
                return _fallback.CreateBuilding(width, height, depth, trim, parent);
            }

            // The caller's requested height is used only to choose which authored block is closest.
            // Scaling to hit it exactly would stretch the facade texture and break the crown's
            // proportions, and would also defeat the batching that having a small fixed set buys.
            var best = _catalog.Buildings[0];
            var bestError = float.MaxValue;

            for (var i = 0; i < _catalog.Buildings.Length; i++)
            {
                var candidate = _catalog.Buildings[i];
                if (candidate == null) continue;

                var error = Mathf.Abs(HeightOf(candidate) - height);
                if (error >= bestError) continue;

                bestError = error;
                best = candidate;
            }

            if (best == null) return _fallback.CreateBuilding(width, height, depth, trim, parent);

            var building = Object.Instantiate(best, parent);
            building.name = "Tower";
            StripCollidersAndShadows(building);
            return building;
        }

        /// <summary>World-space height of a prefab, from the renderer bounds of its whole hierarchy.</summary>
        private static float HeightOf(GameObject prefab)
        {
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            if (renderers.Length == 0) return 0f;

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds.size.y;
        }

        /// <summary>
        /// Applies the same renderer discipline the greybox enforces in
        /// <c>PrimitiveFactory.ConfigureRenderer</c>: no colliders, no shadows, no probes.
        ///
        /// An imported prefab arrives with whatever the importer's defaults were, and those
        /// defaults are wrong for this game twice over. Colliders would hand Unity's broadphase a
        /// few hundred moving colliders to re-index every frame for a game that does not use the
        /// physics engine at all. Shadow casting would add a shadow-map pass per obstacle, which on
        /// a mid-range Android GPU is the difference between 60 and 30 fps — and at speed nobody
        /// reads shadow detail anyway.
        /// </summary>
        private static void StripCollidersAndShadows(GameObject go)
        {
            var colliders = go.GetComponentsInChildren<Collider>(includeInactive: true);
            for (var i = 0; i < colliders.Length; i++)
            {
                PrimitiveFactory.Destroy(colliders[i]);
            }

            var renderers = go.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderers[i].receiveShadows = false;
                renderers[i].lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                renderers[i].reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                renderers[i].motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }
        }
    }
}
