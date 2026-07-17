using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NeonRush.EditorTools.Setup
{
    /// <summary>
    /// Player builds, scripted. One method per build flavour, runnable from the menu or headlessly
    /// from CI — because a build that can only be produced by a human remembering checkbox states
    /// is a build that will eventually ship wrong.
    ///
    /// Headless:
    ///   Unity.exe -batchmode -quit -projectPath . -buildTarget Android
    ///     -executeMethod NeonRush.EditorTools.Setup.BuildScript.BuildAndroidDevApk
    /// </summary>
    public static class BuildScript
    {
        private const string OutputDir = "Builds";

        /// <summary>
        /// The device-test APK: an installable file for sideloading onto a real phone.
        ///
        /// Two deliberate deviations from the store configuration, both scoped to THIS build only:
        ///
        ///  · <b>APK, not App Bundle.</b> The project is configured for AAB because Google Play
        ///    requires it — but an AAB is not directly installable on a device. Sideloading needs
        ///    an APK. The project-wide setting is restored afterwards so a later store build cannot
        ///    silently inherit the test override.
        ///
        ///  · <b>Development build.</b> This flips <c>Debug.isDebugBuild</c> to true, which is what
        ///    lets DevReceiptValidator approve simulated purchases on the device (its release guard
        ///    refuses otherwise — by design). It also enables the profiler, which the first device
        ///    session always ends up wanting. The cost is a small perf overhead and a watermark.
        /// </summary>
        [MenuItem("Neon Rush/Build/Android Dev APK")]
        public static void BuildAndroidDevApk()
        {
            Directory.CreateDirectory(OutputDir);

            var output = Path.Combine(OutputDir, "NeonRush-dev.apk");

            // Remember and override: this flavour must not permanently flip the store setting.
            var previousBundleSetting = EditorUserBuildSettings.buildAppBundle;
            EditorUserBuildSettings.buildAppBundle = false;

            try
            {
                var options = new BuildPlayerOptions
                {
                    scenes = ScenePaths(),
                    locationPathName = output,
                    target = BuildTarget.Android,
                    options = BuildOptions.Development,
                };

                var report = BuildPipeline.BuildPlayer(options);

                if (report.summary.result != BuildResult.Succeeded)
                {
                    Fail($"Build failed: {report.summary.result}, {report.summary.totalErrors} error(s).");
                    return;
                }

                var sizeMb = report.summary.totalSize / (1024f * 1024f);
                Debug.Log($"[BuildScript] APK built: {output} ({sizeMb:F1} MB, {report.summary.totalTime.TotalMinutes:F1} min).");
            }
            finally
            {
                EditorUserBuildSettings.buildAppBundle = previousBundleSetting;
            }
        }

        private static string[] ScenePaths()
        {
            var scenes = EditorBuildSettings.scenes;
            var paths = new System.Collections.Generic.List<string>();

            foreach (var scene in scenes)
            {
                if (scene.enabled) paths.Add(scene.path);
            }

            if (paths.Count == 0)
            {
                Fail("No scenes in Build Settings. Run Neon Rush/Setup/Build Game Scene first.");
            }

            return paths.ToArray();
        }

        private static void Fail(string message)
        {
            Debug.LogError($"[BuildScript] {message}");

            if (UnityEngine.Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
            else
            {
                throw new Exception(message);
            }
        }
    }
}
