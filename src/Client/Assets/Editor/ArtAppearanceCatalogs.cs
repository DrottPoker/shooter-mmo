using System;
using System.IO;
using System.Linq;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Writes the appearance catalog of every exported skinned model that names variants in its
    // asset.json or that a paint layer fits: one slot per painted material with variants, its own
    // image first, and the paint layers of the body in the order they are drawn, as
    // Assets/Art/Appearance/<Model>_Appearance.asset. It writes only catalogs that differ, and
    // ArtExportFollowUp runs it after every export. ArtAppearanceCatalogTests fails when a catalog
    // differs from what this writes. See docs/ART_PIPELINE.md.
    public static class ArtAppearanceCatalogs
    {
        public const string Folder = "Assets/Art/Appearance";
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Update Appearance Catalogs";

        [MenuItem(MenuPath)]
        public static void UpdateAllMenu()
        {
            var written = UpdateAll();
            Debug.Log(written == 0 ? "Every appearance catalog is up to date." : "Wrote " + written + " appearance catalog(s).");
        }

        // Writes every catalog that is missing or differs from its body's export, and returns how
        // many it wrote.
        public static int UpdateAll()
        {
            var written = 0;
            foreach (var manifest in ManifestsWithLooks())
            {
                var path = PathFor(manifest);
                var slots = SlotsFor(manifest);
                var paints = PaintsFor(manifest);
                var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(path);
                if (catalog != null && Matches(catalog, slots, paints))
                {
                    continue;
                }

                if (catalog == null)
                {
                    Directory.CreateDirectory(Folder);
                    catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
                    catalog.Configure(slots, paints);
                    AssetDatabase.CreateAsset(catalog, path);
                }
                else
                {
                    catalog.Configure(slots, paints);
                    EditorUtility.SetDirty(catalog);
                }

                written++;
                Debug.Log("Wrote the appearance catalog " + path + " with " + string.Join(", ",
                    slots.Select(slot => slot.Name + " (" + string.Join(", ", slot.Options.Select(option => option.Name)) + ")")
                        .Concat(paints.Select(paint => "Paint " + paint.Name))));
            }

            if (written > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return written;
        }

        public static bool Matches(BodyAppearanceCatalog catalog, BodyAppearanceCatalog.Slot[] slots, BodyAppearanceCatalog.Paint[] paints)
        {
            return catalog.Slots.Count == slots.Length
                && catalog.Slots.Zip(slots, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Material == expected.Material
                    && actual.Options.Select(option => option.Name).SequenceEqual(expected.Options.Select(option => option.Name))
                    && actual.Options.Select(option => option.Texture).SequenceEqual(expected.Options.Select(option => option.Texture)))
                    .All(same => same)
                && catalog.Paints.Count == paints.Length
                && catalog.Paints.Zip(paints, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Material == expected.Material
                    && actual.Texture == expected.Texture
                    && actual.Layer == expected.Layer
                    && actual.Smoothness.Equals(expected.Smoothness)
                    && actual.Metallic.Equals(expected.Metallic))
                    .All(same => same);
        }

        // Every exported body with variants or with a paint layer that fits it.
        public static ArtAssetManifest[] ManifestsWithLooks()
        {
            var painted = ArtAssetManifests.PaintLayers().SelectMany(paint => paint.bodies).ToArray();
            return ArtAssetManifests.ManifestPaths()
                .Select(ArtAssetManifests.Load)
                .Where(manifest => manifest.IsSkinned && manifest.IsExported
                    && (manifest.variants.Length > 0 || painted.Contains(manifest.AssetPath)))
                .ToArray();
        }

        // The paint layers that fit the body, drawn from the lowest layer up: the paint layer
        // LeatherJerkin over M_Body_Male is the image T_Paint_LeatherJerkin_Male.
        public static BodyAppearanceCatalog.Paint[] PaintsFor(ArtAssetManifest body)
        {
            var label = body.rootObjects.Length == 1 ? body.rootObjects[0] : string.Empty;
            return ArtAssetManifests.PaintLayers()
                .Where(paint => paint.bodies.Contains(body.AssetPath))
                .OrderBy(paint => paint.layer)
                .ThenBy(paint => paint.Name, StringComparer.Ordinal)
                .Select(paint =>
                {
                    var image = "T_Paint_" + paint.Name + "_" + label;
                    var texture = paint.export.textures.Single(record => record.name == image);
                    return new BodyAppearanceCatalog.Paint(
                        paint.Name,
                        ArtModelImportRules.FindProjectMaterial("M_" + paint.part + "_" + label),
                        AssetDatabase.LoadAssetAtPath<Texture2D>(ArtAssetManifests.ToUnityPath(texture.target)),
                        paint.layer,
                        paint.export.smoothness,
                        paint.export.metallic);
                })
                .ToArray();
        }

        public static string PathFor(ArtAssetManifest manifest)
        {
            return Folder + "/" + Path.GetFileNameWithoutExtension(manifest.target) + "_Appearance.asset";
        }

        // M_Face_Male with T_Face_Male_Stubble as its own image and the variant
        // T_Face_Male_Scarred is the slot Face with the options Stubble and Scarred.
        public static BodyAppearanceCatalog.Slot[] SlotsFor(ArtAssetManifest manifest)
        {
            return manifest.variants
                .OrderBy(variant => variant.material, StringComparer.Ordinal)
                .Select(variant =>
                {
                    var parts = variant.material.Split('_');
                    if (parts.Length != 3 || parts[0] != "M")
                    {
                        throw new InvalidOperationException(manifest.path + ": " + variant.material + " is not M_<Part>_<Label>.");
                    }

                    var record = manifest.export.materials.Single(material => material.name == variant.material);
                    var prefix = "T_" + parts[1] + "_" + parts[2] + "_";
                    var images = new[] { record.baseMap }.Concat(variant.images);
                    var options = images
                        .Select(image => new BodyAppearanceCatalog.Option(
                            image.Substring(prefix.Length),
                            AssetDatabase.LoadAssetAtPath<Texture2D>(ArtAssetManifests.ToUnityPath(
                                manifest.export.textures.Single(texture => texture.name == image).target))))
                        .ToArray();
                    return new BodyAppearanceCatalog.Slot(
                        parts[1],
                        ArtModelImportRules.FindProjectMaterial(variant.material),
                        options);
                })
                .ToArray();
        }
    }
}
