using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonRush.Presentation.Visuals
{
    /// <summary>
    /// Builds the grey-box neon look at runtime, with no art assets and no shader files.
    ///
    /// This is scaffolding with a real job: it makes the game playable and *legible* today, so the
    /// core loop can be tuned before a single artist is briefed. Once real art exists, the meshes
    /// and materials are replaced through Addressables and none of the gameplay code changes,
    /// because nothing outside this class knows what an obstacle looks like.
    ///
    /// Materials are cached per colour. Creating a Material per object would give every cube its
    /// own draw call — 300 obstacles becoming 300 draw calls is how a runner drops to 20 fps on a
    /// mid-range Android device. Sharing one material per colour lets the GPU batch them.
    /// </summary>
    public sealed class NeonMaterials : IDisposable
    {
        private readonly Dictionary<Color, Material> _cache = new();
        private readonly Shader _shader;
        private readonly bool _isUrp;

        public NeonMaterials()
        {
            // URP is the target pipeline. The Built-in fallback is not dead code: it keeps the
            // project renderable if the URP asset has not been assigned yet (a fresh clone, or a
            // CI run that skipped pipeline setup). Without it, every material in the game turns
            // magenta and the "bug" looks like a code fault rather than a settings one.
            _shader = Shader.Find("Universal Render Pipeline/Lit");
            _isUrp = _shader != null;

            if (_shader == null)
            {
                _shader = Shader.Find("Standard");
            }

            if (_shader == null)
            {
                throw new InvalidOperationException(
                    "Neither the URP Lit nor the Built-in Standard shader could be found. The render " +
                    "pipeline is misconfigured; nothing will render.");
            }
        }

        // --- The palette. Cyan/magenta on near-black is the classic neon-noir contrast, and it
        // --- has a gameplay job: obstacles must read as danger at a glance, at speed, on a small
        // --- screen in daylight. Coins are the only warm colour in the game, so the eye finds them.

        public static readonly Color Player = new(0.20f, 1.00f, 0.85f);   // cyan
        public static readonly Color Obstacle = new(1.00f, 0.18f, 0.45f); // magenta-red = danger
        public static readonly Color Coin = new(1.00f, 0.82f, 0.25f);     // gold = reward
        public static readonly Color Road = new(0.06f, 0.06f, 0.10f);     // near-black asphalt
        public static readonly Color LaneLine = new(0.35f, 0.25f, 0.85f); // violet lane markers

        /// <summary>Returns a shared, emissive material for <paramref name="colour"/>.</summary>
        public Material Get(Color colour, float emission = 1.6f)
        {
            if (_cache.TryGetValue(colour, out var cached))
            {
                return cached;
            }

            var material = new Material(_shader)
            {
                // Not saved to disk, not part of the scene: this material exists only for this
                // session. Without HideAndDontSave, Unity would leak it into the scene on save.
                hideFlags = HideFlags.HideAndDontSave,
            };

            SetColour(material, colour);

            // Emission is what sells "neon". Values above 1 push the colour into the bloom range so
            // it actually glows rather than just being brightly painted.
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            material.SetColor(EmissionColor, colour * emission);

            _cache[colour] = material;
            return material;
        }

        private void SetColour(Material material, Color colour)
        {
            // URP/Lit and Built-in/Standard expose the albedo under different property names.
            // Setting the wrong one fails silently and leaves everything white.
            if (_isUrp)
            {
                material.SetColor(BaseColor, colour);
            }
            else
            {
                material.SetColor(LegacyColor, colour);
            }
        }

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColor = Shader.PropertyToID("_Color");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        public void Dispose()
        {
            foreach (var material in _cache.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }

            _cache.Clear();
        }
    }
}
