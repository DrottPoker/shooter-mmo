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
    // Assets/Art/Materials/<Category>, the category folder of its model, and Garments for a garment
    // texture. The material of a garment template or texture draws with the garment shader, which
    // takes dyes, cuts its texture at alpha 0.5, and draws both faces, so holes and the inside of a
    // sleeve show. An
    // existing material is never changed, so a material whose Blender look changed is updated by
    // hand from its record; ArtModelImportTests compares every material with its record. Only a
    // Lit material that a paint layer starts to paint, such as a face that gets a scar, takes the
    // CharacterSkin shader, which keeps every Lit value, and a garment texture's material takes the
    // dye mask and natural dye colors of its record, which the pipeline owns like the base map.
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
            var painted = GiveSkinShaderToPaintedMaterials();
            var dyed = GiveDyesToGarmentMaterials();
            Debug.Log(created.Count == 0 && painted.Count == 0 && dyed.Count == 0
                ? "Every exported art material has a project material."
                : string.Join(" ", new[]
                {
                    created.Count > 0 ? "Created " + string.Join(", ", created) + "." : string.Empty,
                    painted.Count > 0 ? "Gave " + string.Join(", ", painted) + " the skin shader for their paint layers." : string.Empty,
                    dyed.Count > 0 ? "Gave " + string.Join(", ", dyed) + " the dyes of their textures." : string.Empty,
                }.Where(text => text.Length > 0)));
        }

        // Gives every Lit material that a paint layer paints the skin shader, and returns their paths.
        public static List<string> GiveSkinShaderToPaintedMaterials()
        {
            var changed = new List<string>();
            foreach (var name in ArtAssetManifests.PaintedMaterialNames())
            {
                var material = ArtModelImportRules.FindProjectMaterial(name);
                if (material == null || material.shader == null || material.shader.name != LitShader)
                {
                    continue;
                }

                material.shader = Shader.Find(SkinShader);
                EditorUtility.SetDirty(material);
                changed.Add(AssetDatabase.GetAssetPath(material));
            }

            AssetDatabase.SaveAssets();
            return changed;
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
                if (!manifest.IsExported || (modelPaths.Length == 0 && !manifest.IsTexture))
                {
                    continue;
                }

                // A garment texture has no model of its own; its material belongs with the garments.
                var category = manifest.IsTexture ? "Garments" : Path.GetFileName(Path.GetDirectoryName(modelPaths[0]));
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
            var shader = manifest.IsGarment || manifest.IsTexture
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
            if (!string.IsNullOrEmpty(record.emission))
            {
                if (!ColorUtility.TryParseHtmlString(record.emission, out var emission))
                {
                    throw new System.InvalidOperationException(
                        manifest.path + ": " + record.name + " has no valid emission.");
                }

                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            if (manifest.IsGarment || manifest.IsTexture)
            {
                SetGarmentSurface(material);
                SetDyes(material, manifest, record);
            }

            return material;
        }

        // Gives every garment material the dye mask and natural dye colors of its export record, so a
        // texture that becomes dyeable, or whose natural colors change, draws as approved. Returns the
        // paths of the changed materials. The dyes come from the pipeline like the base map, so they
        // are set on existing materials too.
        public static List<string> GiveDyesToGarmentMaterials()
        {
            var changed = new List<string>();
            foreach (var manifest in ArtAssetManifests.ManifestPaths().Select(ArtAssetManifests.Load)
                         .Where(manifest => manifest.IsExported && manifest.IsTexture))
            {
                foreach (var record in manifest.export.materials)
                {
                    var material = ArtModelImportRules.FindProjectMaterial(record.name);
                    if (material == null || material.shader == null || material.shader.name != GarmentShader ||
                        HasDyes(material, manifest, record))
                    {
                        continue;
                    }

                    SetDyes(material, manifest, record);
                    EditorUtility.SetDirty(material);
                    changed.Add(AssetDatabase.GetAssetPath(material));
                }
            }

            AssetDatabase.SaveAssets();
            return changed;
        }

        // The dye mask the record names, or null for a texture that is not dyeable.
        public static Texture2D DyeMaskOf(ArtAssetManifest manifest, ArtMaterialRecord record)
        {
            if (string.IsNullOrEmpty(record.dyeMap))
            {
                return null;
            }

            var texture = manifest.export.textures.FirstOrDefault(t => t.name == record.dyeMap);
            var mask = texture != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(ArtAssetManifests.ToUnityPath(texture.target)) : null;
            if (mask == null)
            {
                throw new System.InvalidOperationException(
                    manifest.path + ": the dye mask " + record.dyeMap + " of " + record.name + " is not imported.");
            }

            return mask;
        }

        // Whether a material already has the record's dye mask, channels, and natural colors.
        public static bool HasDyes(Material material, ArtAssetManifest manifest, ArtMaterialRecord record)
        {
            var mask = DyeMaskOf(manifest, record);
            var count = mask != null ? record.dyeColors.Length : 0;
            if (material.GetTexture("_DyeMask") != mask || !Mathf.Approximately(material.GetFloat("_DyeCount"), count))
            {
                return false;
            }

            for (var channel = 0; channel < count; channel++)
            {
                if (!ColorUtility.TryParseHtmlString(record.dyeColors[channel], out var natural) ||
                    material.GetColor("_DyeColor" + (channel + 1)) != natural)
                {
                    return false;
                }
            }

            return true;
        }

        // The dye mask, the number of its channels, and the natural color of each, which the undyed
        // garment wears; channels the texture does not use stay white.
        private static void SetDyes(Material material, ArtAssetManifest manifest, ArtMaterialRecord record)
        {
            var mask = DyeMaskOf(manifest, record);
            var count = mask != null ? record.dyeColors.Length : 0;
            material.SetTexture("_DyeMask", mask);
            material.SetFloat("_DyeCount", count);
            for (var channel = 1; channel <= 3; channel++)
            {
                var color = Color.white;
                if (channel <= count && !ColorUtility.TryParseHtmlString(record.dyeColors[channel - 1], out color))
                {
                    throw new System.InvalidOperationException(
                        manifest.path + ": " + record.name + " has no valid dye color " + channel + ".");
                }

                material.SetColor("_DyeColor" + channel, color);
            }
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
