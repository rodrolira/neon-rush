using UnityEngine;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// Builds the grey-box meshes. Every visual in the game comes from here until real art lands.
    ///
    /// One rule is enforced everywhere in this file: <b>the colliders that Unity's primitives ship
    /// with are stripped immediately.</b> Neon Rush does not use the physics engine at all —
    /// collision is a hand-rolled AABB test in CollisionSystem. Leaving the colliders on would
    /// cost us Unity's broadphase updating hundreds of moving colliders every frame for absolutely
    /// no benefit, and would also silently re-enable physics callbacks we do not want.
    /// </summary>
    public static class PrimitiveFactory
    {
        /// <summary>Creates a cube with the given size and material, with no collider.</summary>
        public static GameObject Cube(string name, Vector3 size, Material material, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            StripCollider(go);

            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;

            ConfigureRenderer(go);

            if (parent != null)
            {
                go.transform.SetParent(parent, worldPositionStays: false);
            }

            return go;
        }

        /// <summary>Creates the coin: a flattened, rotated cylinder, so it reads as a disc.</summary>
        public static GameObject Coin(string name, float radius, Material material, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;

            StripCollider(go);

            // Unity's cylinder stands on its Y axis and is 2 units tall at scale 1. Rotating 90° on
            // X stands it upright facing the camera; scaling Y to a sliver turns it into a coin.
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(radius * 2f, 0.06f, radius * 2f);

            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            ConfigureRenderer(go);

            if (parent != null)
            {
                go.transform.SetParent(parent, worldPositionStays: false);
            }

            return go;
        }

        /// <summary>
        /// Removes the collider Unity attaches to every primitive.
        /// Destroy (not DestroyImmediate) because this runs at play time; DestroyImmediate on a
        /// live object is an Editor-only operation and Unity warns about it in a build.
        /// </summary>
        private static void StripCollider(GameObject go)
        {
            var collider = go.GetComponent<Collider>();

            if (collider == null) return;

            if (UnityEngine.Application.isPlaying)
            {
                Object.Destroy(collider);
            }
            else
            {
                Object.DestroyImmediate(collider);
            }
        }

        /// <summary>
        /// Turns off the renderer features a mobile runner cannot afford.
        ///
        /// Shadows are the expensive one. Every shadow-casting object is an extra pass through the
        /// shadow map; with hundreds of obstacles on screen that alone can halve the frame rate on
        /// a mid-range Android GPU. A runner at speed does not read shadow detail anyway, so this
        /// is pure profit.
        /// </summary>
        private static void ConfigureRenderer(GameObject go)
        {
            var renderer = go.GetComponent<MeshRenderer>();

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }
    }
}
