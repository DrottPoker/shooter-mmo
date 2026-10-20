using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Import rules for every model that tools/Export-ArtAsset.ps1 writes below
    // Assets/Art/Models (see docs/ART_PIPELINE.md). The settings are forced on each import, so
    // no model depends on hand-edited importer settings. Materials keep the names they have in
    // Blender and are remapped to the project material of the same name below
    // Assets/Art/Materials; a model without a matching material keeps an embedded one, which
    // the EditMode tests reject.
    public sealed class ArtModelImportRules : AssetPostprocessor
    {
        public const string ModelFolder = "Assets/Art/Models";
        public const string MaterialFolder = "Assets/Art/Materials";

        public override uint GetVersion()
        {
            return 1;
        }

        public static bool AppliesTo(string assetPath)
        {
            return assetPath.StartsWith(ModelFolder + "/", StringComparison.Ordinal)
                && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        public static void ApplyStaticModelSettings(ModelImporter importer)
        {
            // Blender exports in meters with its own Z-up axes; baking the axis conversion
            // maps Blender +Y to Unity +Z and leaves every object with the rotation it has in
            // Blender. An explicit root keeps every Blender root object a direct child of the
            // model, also when there is only one.
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = true;
            importer.preserveHierarchy = true;
            importer.importCameras = false;
            importer.importLights = false;
            // Objects hidden in Blender, such as a Depleted state, must still import enabled.
            importer.importVisibility = false;
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            // Presentation never takes part in physics; gameplay colliders come from the server.
            importer.addCollider = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.generateSecondaryUV = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        public static Material FindProjectMaterial(string name)
        {
            var matches = AssetDatabase.FindAssets("t:Material " + name, new[] { MaterialFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(path), name, StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1 ? AssetDatabase.LoadAssetAtPath<Material>(matches[0]) : null;
        }

        private void OnPreprocessModel()
        {
            if (AppliesTo(assetPath))
            {
                ApplyStaticModelSettings((ModelImporter)assetImporter);
            }
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets.Where(AppliesTo))
            {
                RemapEmbeddedMaterials(path);
            }
        }

        private static void RemapEmbeddedMaterials(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
            {
                return;
            }

            var remapped = false;
            foreach (var embedded in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
            {
                var projectMaterial = FindProjectMaterial(embedded.name);
                if (projectMaterial != null)
                {
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(embedded), projectMaterial);
                    remapped = true;
                }
            }

            // The reimport has no embedded material left for these names, so this runs once.
            if (remapped)
            {
                importer.SaveAndReimport();
            }
        }
    }
}
