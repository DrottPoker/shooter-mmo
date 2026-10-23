using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Writes the appearance catalog of every exported skinned model that names variants in its
    // asset.json or that a paint layer or a garment fits: one slot per painted material with
    // variants, its own image first, the paint layers of the body in the order they are drawn, the
    // body parts with the faces of the editable mesh their triangles come from, and the garments of
    // the body from the lowest layer up, as Assets/Art/Appearance/<Model>_Appearance.asset. It
    // writes only catalogs that differ, and ArtExportFollowUp runs it after every export.
    // ArtAppearanceCatalogTests fails when a catalog differs from what this writes. See
    // docs/ART_PIPELINE.md.
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
                var parts = PartsFor(manifest);
                var garments = GarmentsFor(manifest);
                var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(path);
                if (catalog != null && Matches(catalog, slots, paints) && Matches(catalog, parts, garments))
                {
                    continue;
                }

                if (catalog == null)
                {
                    Directory.CreateDirectory(Folder);
                    catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
                    catalog.Configure(slots, paints, parts, garments);
                    AssetDatabase.CreateAsset(catalog, path);
                }
                else
                {
                    catalog.Configure(slots, paints, parts, garments);
                    EditorUtility.SetDirty(catalog);
                }

                written++;
                Debug.Log("Wrote the appearance catalog " + path + " with " + string.Join(", ",
                    slots.Select(slot => slot.Name + " (" + string.Join(", ", slot.Options.Select(option => option.Name)) + ")")
                        .Concat(paints.Select(paint => "Paint " + paint.Name))
                        .Concat(garments.Select(garment => "Garment " + garment.Name))));
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

        public static bool Matches(BodyAppearanceCatalog catalog, BodyAppearanceCatalog.Part[] parts,
            BodyAppearanceCatalog.Garment[] garments)
        {
            return catalog.Parts.Count == parts.Length
                && catalog.Parts.Zip(parts, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.TriangleFaces.SequenceEqual(expected.TriangleFaces)
                    && actual.VertexSources.SequenceEqual(expected.VertexSources))
                    .All(same => same)
                && catalog.Garments.Count == garments.Length
                && catalog.Garments.Zip(garments, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Slot == expected.Slot
                    && actual.Layer == expected.Layer
                    && actual.Hides.SequenceEqual(expected.Hides)
                    && actual.Hair == expected.Hair
                    && actual.Cloth == expected.Cloth
                    && actual.Model == expected.Model
                    && actual.Covered.SequenceEqual(expected.Covered)
                    && actual.Underlay.SequenceEqual(expected.Underlay)
                    && actual.PullInVertices.SequenceEqual(expected.PullInVertices)
                    && actual.PullInDistances.SequenceEqual(expected.PullInDistances))
                    .All(same => same);
        }

        // Every exported body with variants, or with a paint layer or a garment that fits it.
        public static ArtAssetManifest[] ManifestsWithLooks()
        {
            var painted = ArtAssetManifests.PaintLayers().SelectMany(paint => paint.bodies).ToArray();
            var dressed = ArtAssetManifests.Garments().SelectMany(garment => garment.bodies).ToArray();
            return ArtAssetManifests.ManifestPaths()
                .Select(ArtAssetManifests.Load)
                .Where(manifest => manifest.IsSkinned && manifest.IsExported
                    && (manifest.variants.Length > 0 || painted.Contains(manifest.AssetPath) || dressed.Contains(manifest.AssetPath)))
                .ToArray();
        }

        // The editable mesh a body exports next to its model, or null for a body without one.
        public static ArtTopologyMaps.Topology TopologyFor(ArtAssetManifest body)
        {
            var path = body.UnityDataPath;
            var asset = path != null ? AssetDatabase.LoadAssetAtPath<TextAsset>(path) : null;
            if (asset == null)
            {
                return null;
            }

            var topology = ArtBodyTopology.FromJson(asset.text);
            return new ArtTopologyMaps.Topology(topology.vertices, topology.faceSizes, topology.faceVertices);
        }

        // Every part of the body's model that comes from its editable mesh, with the face each of
        // its triangles comes from and the vertex each of its vertices is. The eyes, teeth, and
        // tongue are meshes of their own and no part.
        public static BodyAppearanceCatalog.Part[] PartsFor(ArtAssetManifest body)
        {
            var topology = TopologyFor(body);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(body.UnityTargetPath);
            if (topology == null || model == null)
            {
                return Array.Empty<BodyAppearanceCatalog.Part>();
            }

            var parts = new List<BodyAppearanceCatalog.Part>();
            foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderBy(r => r.name, StringComparer.Ordinal))
            {
                var where = body.UnityTargetPath + " " + renderer.name;
                var sources = ArtTopologyMaps.VertexSources(PositionsIn(model.transform, renderer), topology);
                if (sources.All(source => source < 0))
                {
                    continue;
                }

                if (sources.Any(source => source < 0))
                {
                    throw new InvalidOperationException(where + " has vertices that are not in the editable mesh; export the body again.");
                }

                parts.Add(new BodyAppearanceCatalog.Part(
                    renderer.name,
                    ArtTopologyMaps.TriangleFaces(renderer.sharedMesh, sources, topology, where),
                    sources));
            }

            return parts.ToArray();
        }

        // The garments that fit the body, from the lowest layer up, each with its version for the
        // body: its model, and its data with the underlay of every triangle of the model.
        public static BodyAppearanceCatalog.Garment[] GarmentsFor(ArtAssetManifest body)
        {
            var label = body.rootObjects.Length == 1 ? body.rootObjects[0] : string.Empty;
            return ArtAssetManifests.Garments()
                .Where(garment => garment.bodies.Contains(body.AssetPath))
                .OrderBy(garment => garment.layer)
                .ThenBy(garment => garment.Name, StringComparer.Ordinal)
                .Select(garment =>
                {
                    var record = garment.export.models.Single(model => model.body == label);
                    var where = garment.path + " " + record.body;
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(ArtAssetManifests.ToUnityPath(record.target));
                    var text = AssetDatabase.LoadAssetAtPath<TextAsset>(record.UnityDataPath);
                    if (model == null || text == null)
                    {
                        throw new InvalidOperationException(where + ": the model or its data is not imported.");
                    }

                    var data = ArtGarmentData.FromJson(text.text);
                    var topology = new ArtTopologyMaps.Topology(data.garmentVertices, data.garmentFaceSizes, data.garmentFaceVertices);
                    var renderer = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
                    var sources = ArtTopologyMaps.VertexSources(PositionsIn(model.transform, renderer), topology);
                    if (sources.Any(source => source < 0))
                    {
                        throw new InvalidOperationException(where + ": the model has vertices that are not in its data; export the garment again.");
                    }

                    var faces = ArtTopologyMaps.TriangleFaces(renderer.sharedMesh, sources, topology, where);
                    return new BodyAppearanceCatalog.Garment(
                        garment.Name,
                        garment.slot,
                        garment.layer,
                        garment.hides,
                        garment.hair,
                        garment.cloth,
                        model,
                        data.covered,
                        faces.Select(face => data.underlay[face]).ToArray(),
                        data.pullInVertices,
                        data.pullInDistances);
                })
                .ToArray();
        }

        // The renderer's vertices in the model root's space.
        private static Vector3[] PositionsIn(Transform root, SkinnedMeshRenderer renderer)
        {
            var toRoot = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            return renderer.sharedMesh.vertices.Select(vertex => toRoot.MultiplyPoint3x4(vertex)).ToArray();
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
