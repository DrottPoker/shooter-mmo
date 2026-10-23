using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // Creates the project material of every exported Blender material that has none yet, as a
    // URP Lit material, or a CharacterSkin material when a paint layer paints it, with the values
    // of its export record, below
    // Assets/Art/Materials/<Category>, the category folder of its model. A garment's material draws
    // with the garment shader, which takes dyes, cuts its texture at alpha 0.5, and draws both
    // faces, so holes and the inside of a sleeve show. An
    // existing material is never changed, so a material whose Blender look changed is updated by
    // hand from its record; ArtModelImportTests compares every material with its record.
    public static class ArtMaterialCreator
    {
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Create Missing Materials";

        public const string LitShader = "Universal Render Pipeline/Lit";
        public const string SkinShader = "Mmorpg/Character Skin";
        public const string GarmentShader = "Mmorpg/Garment";

        // Where a garment's texture cuts, as the cover masks read it.
        public const float GarmentAlphaCutoff = 0.5f;

        [MenuItem(MenuPath)]
        public static void CreateMissingMaterialsMenu()
        {
            var created = CreateMissingMaterials();
            Debug.Log(created.Count == 0
                ? "Every exported art material has a project material."
                : "Created " + string.Join(", ", created) + ".");
        }

        // Returns the paths of the created materials and reimports the models that use them, so
        // their materials are remapped.
        public static List<string> CreateMissingMaterials()
        {
            var created = new List<string>();
            var models = new List<string>();
            foreach (var manifest in ArtAssetManifests.ManifestPaths().Select(ArtAssetManifests.Load))
            {
                var modelPaths = manifest.UnityModelPaths;
                if (!manifest.IsExported || modelPaths.Length == 0)
                {
                    continue;
                }

                var category = Path.GetFileName(Path.GetDirectoryName(modelPaths[0]));
                var folder = ArtModelImportRules.MaterialFolder + "/" + category;
                foreach (var record in manifest.export.materials)
                {
                    if (ArtModelImportRules.FindProjectMaterial(record.name) != null)
                    {
                        continue;
                    }

                    EnsureFolder(folder);
                    var path = folder + "/" + record.name + ".mat";
                    AssetDatabase.CreateAsset(CreateMaterial(manifest, record), path);
                    created.Add(path);
                    models.AddRange(modelPaths.Where(model => !models.Contains(model)));
                }
            }

            AssetDatabase.SaveAssets();
            foreach (var model in models)
            {
                AssetDatabase.ImportAsset(model, ImportAssetOptions.ForceUpdate);
            }

            return created;
        }

        private static Material CreateMaterial(ArtAssetManifest manifest, ArtMaterialRecord record)
        {
            var shader = manifest.IsGarment
                ? GarmentShader
                : ArtAssetManifests.PaintedMaterialNames().Contains(record.name) ? SkinShader : LitShader;
            var material = new Material(Shader.Find(shader)) { name = record.name };
            if (!ColorUtility.TryParseHtmlString(record.baseColor, out var baseColor))
            {
                throw new System.InvalidOperationException(
                    manifest.path + ": " + record.name + " has no valid baseColor.");
            }

            material.SetColor("_BaseColor", baseColor);
            if (!string.IsNullOrEmpty(record.baseMap))
            {
                var texture = manifest.export.textures.FirstOrDefault(t => t.name == record.baseMap);
                var texturePath = texture != null ? ArtAssetManifests.ToUnityPath(texture.target) : null;
                var image = texturePath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath) : null;
                if (image == null)
                {
                    throw new System.InvalidOperationException(
                        manifest.path + ": the texture " + record.baseMap + " of " + record.name + " is not imported.");
                }

                material.SetTexture("_BaseMap", image);
            }

            material.SetFloat("_Metallic", record.metallic);
            material.SetFloat("_Smoothness", record.smoothness);
            if (manifest.IsGarment)
            {
                SetGarmentSurface(material);
            }

            return material;
        }

        // An opaque surface cut at the garment's alpha and drawn from both sides, as the URP Lit
        // inspector sets it up.
        public static void SetGarmentSurface(Material material)
        {
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", GarmentAlphaCutoff);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)RenderQueue.AlphaTest;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
