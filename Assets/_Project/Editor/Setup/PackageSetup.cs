using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace NeonRush.EditorTools.Setup
{
    /// <summary>
    /// One-shot project provisioning: installs the package set Neon Rush depends on.
    ///
    /// Versions are deliberately NOT pinned here. Unity's Package Manager resolves the
    /// highest version compatible with the current editor, which keeps this script valid
    /// across editor upgrades. The resolved versions are then locked by Unity into
    /// Packages/packages-lock.json, which IS committed — that file is the reproducible
    /// build guarantee, not a hand-maintained version list in this script.
    ///
    /// Run headlessly:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod NeonRush.EditorTools.Setup.PackageSetup.InstallAll
    /// </summary>
    public static class PackageSetup
    {
        /// <summary>Packages required by the shipping game and its CI pipeline.</summary>
        private static readonly string[] RequiredPackages =
        {
            "com.unity.render-pipelines.universal", // URP — mobile-tuned scriptable render pipeline
            "com.unity.inputsystem",                // New Input System — swipe/touch handling
            "com.unity.addressables",               // Remote content delivery, seasonal asset drops
            "com.unity.test-framework",             // NUnit EditMode/PlayMode tests
            "com.unity.testtools.codecoverage",     // Coverage reporting in CI
            "com.unity.nuget.newtonsoft-json",      // Save serialisation + Firebase dependency
            "com.unity.purchasing",                 // Wraps Google Play Billing + StoreKit
            "com.unity.mobile.notifications",       // Local notifications (complements FCM)
        };

        /// <summary>
        /// Packages the default template ships with that Neon Rush does not use.
        /// Removing them trims build size and Editor import time.
        /// </summary>
        private static readonly string[] UnwantedPackages =
        {
            "com.unity.multiplayer.center", // Neon Rush is single-player; this is template cruft.
        };

        // A Package Manager request resolves on a background thread. In -batchmode the main
        // thread is not pumping an Editor update loop, so we block on IsCompleted rather than
        // subscribing to EditorApplication.update (which would never fire and hang the build).
        private const int PollIntervalMs = 100;
        private const int TimeoutMs = 10 * 60 * 1000;

        [MenuItem("Neon Rush/Setup/Install Required Packages")]
        public static void InstallAll()
        {
            var failures = new List<string>();

            foreach (var package in UnwantedPackages)
            {
                if (!TryAwait(Client.Remove(package), out var removeError))
                {
                    // A package that was never installed is not an error worth failing the build over.
                    Debug.Log($"[PackageSetup] Skipped removal of '{package}': {removeError}");
                }
                else
                {
                    Debug.Log($"[PackageSetup] Removed '{package}'.");
                }
            }

            foreach (var package in RequiredPackages)
            {
                if (TryAwait(Client.Add(package), out var addError))
                {
                    Debug.Log($"[PackageSetup] Installed '{package}'.");
                }
                else
                {
                    Debug.LogError($"[PackageSetup] FAILED to install '{package}': {addError}");
                    failures.Add($"{package} ({addError})");
                }
            }

            if (failures.Count > 0)
            {
                // Fail the CI job loudly. A half-provisioned project produces confusing
                // downstream compile errors that look like code bugs but are not.
                Fail($"{failures.Count} package(s) failed to install: {string.Join(", ", failures)}");
                return;
            }

            Client.Resolve();
            Debug.Log("[PackageSetup] All packages installed and resolved.");
        }

        /// <summary>Blocks until <paramref name="request"/> settles. Returns true on success.</summary>
        private static bool TryAwait(Request request, out string error)
        {
            var waited = 0;
            while (!request.IsCompleted)
            {
                Thread.Sleep(PollIntervalMs);
                waited += PollIntervalMs;

                if (waited < TimeoutMs) continue;

                error = $"timed out after {TimeoutMs / 1000}s";
                return false;
            }

            if (request.Status == StatusCode.Success)
            {
                error = null;
                return true;
            }

            error = request.Error?.message ?? "unknown Package Manager error";
            return false;
        }

        /// <summary>
        /// Terminates the headless Editor with a non-zero exit code so CI actually goes red.
        /// Debug.LogError alone does not fail a -batchmode run.
        /// </summary>
        private static void Fail(string message)
        {
            Debug.LogError($"[PackageSetup] {message}");

            // Fully qualified deliberately: this project defines a `NeonRush.Application` namespace,
            // so a bare `Application` binds to that instead of UnityEngine.Application and fails to
            // compile. Anything in an editor script touching UnityEngine.Application must qualify it.
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
