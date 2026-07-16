using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// The atmosphere pass: bloom, vignette and fog. This is where "coloured cubes" becomes "neon".
    ///
    /// Everything here is built at runtime from code, consistent with the rest of the project — no
    /// Volume profile asset to lose references to. The values are deliberately restrained: bloom is
    /// a spice, and over-blooming is the single most common way hobbyist neon looks cheap. The glow
    /// should read as light bleeding off signage, not as smearing vaseline on the lens.
    /// </summary>
    public static class NeonAtmosphere
    {
        /// <summary>Deep violet-black. Shared by the camera background and the fog so they blend seamlessly.</summary>
        public static readonly Color Horizon = new(0.03f, 0.015f, 0.08f);

        /// <summary>Configures the camera, the global volume, and the scene fog.</summary>
        public static void Setup(Camera camera, Transform parent, float farPlane)
        {
            ConfigureCamera(camera);
            BuildVolume(parent);
            ConfigureFog(farPlane);
        }

        private static void ConfigureCamera(Camera camera)
        {
            camera.backgroundColor = Horizon;

            // Post-processing is opt-in per camera in URP. Without this line the volume exists,
            // the bloom is configured, and absolutely nothing renders differently.
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;

            // Cheap AA on the camera; pairs with the pipeline's 2x MSAA.
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
        }

        private static void BuildVolume(Transform parent)
        {
            var go = new GameObject("PostFX", typeof(Volume));
            go.transform.SetParent(parent, worldPositionStays: false);

            var volume = go.GetComponent<Volume>();
            volume.isGlobal = true;

            // Created at runtime and never saved: HideAndDontSave keeps Unity from trying to
            // persist a profile that belongs to this session only.
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;

            var bloom = profile.Add<Bloom>();

            // Threshold just under 1.0: only genuinely emissive surfaces glow. Raising intensity
            // instead of lowering threshold is the discipline that keeps the road and UI crisp
            // while the neon strips burn.
            bloom.threshold.Override(0.9f);
            bloom.intensity.Override(0.9f);
            bloom.scatter.Override(0.55f);

            // High-quality filtering is a real cost on mobile GPUs and at this art style the
            // difference is invisible at speed.
            bloom.highQualityFiltering.Override(false);

            var vignette = profile.Add<Vignette>();

            // A soft dark edge does two jobs: it focuses the eye down the track (where the
            // decisions are), and it hides the emptiness at the screen corners that grey-box
            // environments otherwise expose.
            vignette.intensity.Override(0.28f);
            vignette.smoothness.Override(0.45f);
            vignette.color.Override(Color.black);

            volume.profile = profile;
        }

        private static void ConfigureFog(float farPlane)
        {
            // Fog in the exact horizon colour is what turns "chunks popping into existence at the
            // far plane" into "the city emerging from the haze". The far plane is fully fogged, so
            // spawn-in is structurally invisible rather than merely unlikely.
            RenderSettings.fog = true;
            RenderSettings.fogColor = Horizon;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = farPlane * 0.35f;
            RenderSettings.fogEndDistance = farPlane * 0.95f;
        }
    }
}
