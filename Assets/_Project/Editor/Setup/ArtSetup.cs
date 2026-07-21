using System.IO;
using NeonRush.Presentation.Visuals;
using UnityEditor;
using UnityEngine;

namespace NeonRush.EditorTools.Setup
{
    /// <summary>
    /// Wires the authored art into the game, in the same one-click, re-runnable style as the rest
    /// of the setup menu.
    ///
    /// Two jobs, and both exist because they are the steps most likely to be got wrong by hand:
    ///
    ///  · <b>Creating the catalog in the right place.</b> The bootstrap loads it via
    ///    <c>Resources.Load</c>, which fails silently and falls back to the greybox if the asset is
    ///    a folder off. "The art does not show up and nothing is logged as an error" is a bad
    ///    afternoon; putting the path in code removes the possibility.
    ///  · <b>Applying the import settings.</b> An imported model arrives with colliders and shadow
    ///    casting on, both of which are actively wrong for this game — it uses no physics engine at
    ///    all, and shadows are the single most expensive thing a mobile renderer can be asked for.
    /// </summary>
    public static class ArtSetup
    {
        private const string CatalogFolder = "Assets/_Project/Resources/NeonRush";
        private const string CatalogPath = CatalogFolder + "/ModelCatalog.asset";
        private const string ModelRoot = "Assets/_Project/Art/Models";

        [MenuItem("Neon Rush/Setup/Create Model Catalog")]
        public static void CreateCatalog()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ModelCatalog>(CatalogPath);

            if (existing != null)
            {
                Debug.Log($"[NeonRush] Model catalog already exists at {CatalogPath}.");
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                return;
            }

            Directory.CreateDirectory(CatalogFolder);
            AssetDatabase.Refresh();

            var catalog = ScriptableObject.CreateInstance<ModelCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[NeonRush] Created {CatalogPath}. Assign the imported models to it; " +
                      "any field left empty keeps using the greybox for that object.");

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        /// <summary>
        /// Forces every model under Art/Models onto the import settings this game needs.
        ///
        /// Safe to re-run, and worth re-running after any batch re-export: the importer resets to
        /// its defaults for newly added files, so a single new model can quietly reintroduce
        /// colliders and shadows that nobody notices until the frame rate drops on device.
        /// </summary>
        [MenuItem("Neon Rush/Setup/Apply Model Import Settings")]
        public static void ApplyImportSettings()
        {
            if (!Directory.Exists(ModelRoot))
            {
                Debug.LogWarning($"[NeonRush] No models found at {ModelRoot}; nothing to configure.");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Model", new[] { ModelRoot });
            var changed = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;

                if (importer == null) continue;

                // Models are authored in metres and exported at true size, so any scale factor
                // other than 1 silently desynchronises the art from ObstacleArchetype.
                importer.globalScale = 1f;
                importer.addCollider = false;

                // Nothing in this project is animated or skinned yet, and importing rigs the game
                // does not use costs load time and memory for every single model.
                importer.animationType = ModelImporterAnimationType.None;
                importer.importAnimation = false;
                importer.importBlendShapes = false;
                importer.importCameras = false;
                importer.importLights = false;

                // Meshes are never read back on the CPU. Leaving Read/Write on keeps a second copy
                // of every mesh in system memory for no benefit.
                importer.isReadable = false;
                importer.meshCompression = ModelImporterMeshCompression.Medium;
                importer.optimizeMeshPolygons = true;
                importer.optimizeMeshVertices = true;
                importer.weldVertices = true;

                importer.SaveAndReimport();
                changed++;
            }

            Debug.Log($"[NeonRush] Applied import settings to {changed} models under {ModelRoot}.");
        }
    }
}
