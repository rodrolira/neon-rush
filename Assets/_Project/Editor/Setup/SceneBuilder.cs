using System.Collections.Generic;
using System.IO;
using NeonRush.Composition;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NeonRush.EditorTools.Setup
{
    /// <summary>
    /// Generates the game scene.
    ///
    /// The scene is deliberately almost empty: one GameObject carrying <see cref="GameBootstrap"/>,
    /// which builds the entire game — player, track, camera, lighting, UI — from code at runtime.
    ///
    /// This is not laziness, it is a considered trade. A scene is a large YAML file full of GUIDs.
    /// It cannot be reviewed in a pull request, it merges catastrophically when two people touch it,
    /// and a reference that goes missing produces a NullReferenceException that only appears when a
    /// player runs the game. Building the scene from code means the composition is diffable,
    /// mergeable, and its mistakes are compile errors.
    ///
    /// The trade-off, stated honestly: designers cannot drag things around in the Editor to tune the
    /// layout. That is acceptable now, because there is no art to lay out. When real content arrives
    /// it will come through Addressables as data, which is the mechanism the brief asks for anyway.
    ///
    /// Run headlessly:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod NeonRush.EditorTools.Setup.SceneBuilder.BuildGameScene
    /// </summary>
    public static class SceneBuilder
    {
        private const string ScenesDir = "Assets/_Project/Scenes";
        private const string GameScenePath = ScenesDir + "/Game.unity";

        [MenuItem("Neon Rush/Setup/Build Game Scene")]
        public static void BuildGameScene()
        {
            Directory.CreateDirectory(ScenesDir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var bootstrap = new GameObject("[GameBootstrap]");
            bootstrap.AddComponent<GameBootstrap>();

            // No camera, no light, no EventSystem authored here. GameBootstrap creates them, so
            // there is exactly one place that decides what the scene contains — and no chance of the
            // scene and the code disagreeing about, say, which camera is the main one.

            EditorSceneManager.SaveScene(scene, GameScenePath);

            AddToBuildSettings(GameScenePath);

            Debug.Log($"[SceneBuilder] Created {GameScenePath} and set it as the startup scene.");
        }

        /// <summary>Ensures the scene is in Build Settings and is index 0, so a build boots into it.</summary>
        private static void AddToBuildSettings(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

            scenes.RemoveAll(s => s.path == scenePath);

            // Index 0 is the scene Unity loads on launch. Anything else and the built game opens to
            // a black screen — a classic, and classically confusing, shipping bug.
            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, enabled: true));

            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
