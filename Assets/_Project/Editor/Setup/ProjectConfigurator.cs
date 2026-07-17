using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NeonRush.EditorTools.Setup
{
    /// <summary>
    /// Applies every project-wide setting Neon Rush depends on, in one idempotent, re-runnable pass.
    ///
    /// This exists instead of a page of "now click these 30 checkboxes" in the README. Project
    /// settings live in YAML files that merge badly and are never reviewed; when they drift, the
    /// symptom is a 40 MB APK, or a game locked at 30 fps, or magenta materials — and nobody can
    /// tell you which checkbox did it. Encoding them as code makes them diffable, reviewable and
    /// reproducible on a fresh clone or a CI runner.
    ///
    /// Run headlessly:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod NeonRush.EditorTools.Setup.ProjectConfigurator.ConfigureAll
    /// </summary>
    public static class ProjectConfigurator
    {
        private const string SettingsDir = "Assets/_Project/Settings";
        private const string RendererPath = SettingsDir + "/NeonRush_Renderer.asset";
        private const string PipelinePath = SettingsDir + "/NeonRush_URP.asset";

        [MenuItem("Neon Rush/Setup/Configure Project")]
        public static void ConfigureAll()
        {
            ConfigureIdentity();
            ConfigureRendering();
            ConfigureInputSystem();
            ConfigureAndroid();
            ConfigureIos();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[ProjectConfigurator] Project configured.");
        }

        private static void ConfigureIdentity()
        {
            PlayerSettings.companyName = "MoonCat Studio";
            PlayerSettings.productName = "Neon Rush";

            // Reverse-DNS bundle id. This is the single hardest thing to change later: it is the
            // primary key for the app on both stores, for Firebase, and for AdMob. Getting it wrong
            // and shipping means a new app listing and the loss of every install and review.
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.mooncatstudio.neonrush");
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.mooncatstudio.neonrush");

            PlayerSettings.bundleVersion = "0.1.0";
        }

        private static void ConfigureRendering()
        {
            // Linear colour space. Gamma is the Unity default and it is simply wrong for a game
            // whose entire look is emissive neon over near-black: in gamma, bright emissive colours
            // clip and bloom looks like a white smear instead of a glow.
            PlayerSettings.colorSpace = ColorSpace.Linear;

            Directory.CreateDirectory(SettingsDir);

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            // Mobile budget. Every one of these is a frame-rate decision, not a taste decision.
            //
            // HDR is the one deliberate exception: it costs real framebuffer bandwidth, but the
            // entire art direction is emissive neon, and neon without bloom is just brightly
            // painted plastic. Bloom needs HDR to have anything to bloom. If telemetry shows
            // low-end devices struggling, this becomes a Remote Config quality toggle — never a
            // reason to ship a look that doesn't sell the game.
            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 2;              // 2x is nearly free on tile-based mobile GPUs; 4x is not.
            pipeline.shadowDistance = 0f;              // No real-time shadows at all. See GameBootstrap.
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;

            // The renderer needs the URP post-process data asset, or Volume/Bloom silently render
            // nothing — a classic "why is my bloom invisible" that produces no error anywhere.
            if (renderer.postProcessData == null)
            {
                renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                    "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");

                EditorUtility.SetDirty(renderer);
            }

            // Render at a slightly reduced resolution on very high-DPI phones. A 1440p runner is
            // fragment-bound on almost every Android GPU; 80% render scale is close to invisible at
            // speed and buys a large fraction of the frame budget back.
            pipeline.renderScale = 0.85f;

            EditorUtility.SetDirty(pipeline);

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            EnsureRuntimeShadersIncluded();
        }

        /// <summary>
        /// Forces the shaders the game resolves at runtime into the build's "Always Included Shaders".
        ///
        /// Neon Rush builds every material in code via <c>Shader.Find("Universal Render Pipeline/Lit")</c>
        /// (see NeonMaterials). No material asset in any scene references that shader, so Unity's build
        /// pipeline — correctly, from its point of view — strips it as unused. On device <c>Shader.Find</c>
        /// then returns null, NeonMaterials throws, and the player sees a black screen with a red error.
        /// It never reproduces in the Editor, where every shader is always loaded. This is the canonical
        /// "works in Editor, black on phone" trap for a game with no baked materials, and the fix is to
        /// pin the shader here so it ships regardless of what references it.
        /// </summary>
        private static void EnsureRuntimeShadersIncluded()
        {
            // Every runtime material routes through this one shader (NeonMaterials.Get). If more
            // Shader.Find names appear later (e.g. an Unlit trail), add them here — the symptom is
            // always the same black screen.
            string[] required = { "Universal Render Pipeline/Lit" };

            var serialized = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            var list = serialized.FindProperty("m_AlwaysIncludedShaders");

            foreach (var name in required)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    Debug.LogWarning($"[ProjectConfigurator] Shader '{name}' not found in the Editor; cannot pin it for the build.");
                    continue;
                }

                if (IsAlreadyIncluded(list, shader)) continue;

                var i = list.arraySize;
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).objectReferenceValue = shader;
                Debug.Log($"[ProjectConfigurator] Pinned '{name}' into Always Included Shaders so it survives the build.");
            }

            serialized.ApplyModifiedProperties();
        }

        private static bool IsAlreadyIncluded(SerializedProperty list, Shader shader)
        {
            for (var i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return true;
            }

            return false;
        }

        /// <summary>
        /// Switches the project to the new Input System.
        ///
        /// There is no public API for this — <c>activeInputHandler</c> is a private field on the
        /// ProjectSettings asset — so it is set through SerializedObject. Value 2 = "Both", which
        /// enables the new Input System without breaking any code still calling legacy
        /// <c>UnityEngine.Input</c>. Leaving it on the default (legacy only) makes every
        /// <c>Touchscreen.current</c> in the game return null, and the game silently does not respond
        /// to touch at all — a bug that looks like broken input code rather than a project setting.
        /// </summary>
        private static void ConfigureInputSystem()
        {
            const int both = 2;

            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[ProjectConfigurator] Could not open ProjectSettings.asset; input handler unchanged.");
                return;
            }

            var settings = new SerializedObject(assets[0]);
            var property = settings.FindProperty("activeInputHandler");

            if (property == null)
            {
                Debug.LogWarning("[ProjectConfigurator] 'activeInputHandler' not found; input handler unchanged.");
                return;
            }

            if (property.intValue == both) return;

            property.intValue = both;
            settings.ApplyModifiedProperties();

            Debug.Log("[ProjectConfigurator] Input handler set to Both. The Editor must be restarted for this to take effect.");
        }

        private static void ConfigureAndroid()
        {
            // Android 8.0. Below this, Google Play policy and the Firebase SDK both become painful,
            // and the devices concerned cannot hold 60 fps anyway.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            // IL2CPP + ARM64 is mandatory: Google Play has required 64-bit binaries since 2019, and
            // Mono is not an option for a release build.
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.ARMv7;

            // App Bundle, not APK — required for Play Store submission.
            EditorUserBuildSettings.buildAppBundle = true;

            PlayerSettings.Android.useCustomKeystore = false; // CI injects the real keystore.

            // Portrait only. The whole game — lanes, HUD, thumb reach — is designed for one hand in
            // portrait, and supporting landscape would mean a second UI layout for zero revenue.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;

            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Medium);
        }

        private static void ConfigureIos()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.iOS.targetOSVersionString = "13.0";
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Medium);
        }
    }
}
