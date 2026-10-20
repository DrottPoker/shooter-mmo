using System;
using System.IO;
using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Models below Assets/Art/Models come from tools/Export-ArtAsset.ps1 and must import with
    // ArtModelImportRules; see docs/ART_PIPELINE.md.
    public sealed class ArtModelImportTests
    {
        // asset.json targets are repository paths; Unity asset paths start at Assets/.
        private const string ClientProjectPrefix = "src/Client/";

        private static string[] ModelPaths()
        {
            return AssetDatabase.FindAssets("t:Model", new[] { ArtModelImportRules.ModelFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(ArtModelImportRules.AppliesTo)
                .ToArray();
        }

        [Test]
        public void EveryArtModelUsesTheStaticImportRules()
        {
            var paths = ModelPaths();
            Assert.That(paths, Is.Not.Empty);
            foreach (var path in paths)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                Assert.That(importer.globalScale, Is.EqualTo(1f), path);
                Assert.That(importer.useFileScale, Is.True, path);
                Assert.That(importer.bakeAxisConversion, Is.True, path);
                Assert.That(importer.preserveHierarchy, Is.True, path);
                Assert.That(importer.importCameras, Is.False, path);
                Assert.That(importer.importLights, Is.False, path);
                Assert.That(importer.importVisibility, Is.False, path);
                Assert.That(importer.importAnimation, Is.False, path);
                Assert.That(importer.animationType, Is.EqualTo(ModelImporterAnimationType.None), path);
                Assert.That(importer.addCollider, Is.False, path);
                Assert.That(importer.isReadable, Is.False, path);
                Assert.That(importer.importNormals, Is.EqualTo(ModelImporterNormals.Import), path);
            }
        }

        [Test]
        public void EveryArtModelUsesProjectMaterials()
        {
            foreach (var path in ModelPaths())
            {
                Assert.That(
                    AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Select(m => m.name),
                    Is.Empty,
                    path + " embeds materials; name them in Blender like a material in "
                        + ArtModelImportRules.MaterialFolder);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                Assert.That(renderers, Is.Not.Empty, path);
                foreach (var renderer in renderers)
                {
                    Assert.That(renderer.sharedMaterials, Is.Not.Empty, path + " " + renderer.name);
                    foreach (var material in renderer.sharedMaterials)
                    {
                        Assert.That(material, Is.Not.Null, path + " " + renderer.name);
                        Assert.That(
                            AssetDatabase.GetAssetPath(material),
                            Does.StartWith(ArtModelImportRules.MaterialFolder + "/"),
                            path + " " + renderer.name);
                    }
                }
            }
        }

        [Test]
        public void EveryArtMaterialMatchesTheApprovedBlenderMaterial()
        {
            // The export records the approved Blender look of every material in asset.json, so
            // the game shows the colors the reviewer approved.
            var manifests = Directory.GetFiles(
                Path.Combine(RepositoryLayout.FindRoot(), "art"),
                "asset.json",
                SearchOption.AllDirectories);
            Assert.That(manifests, Is.Not.Empty);
            foreach (var manifestPath in manifests)
            {
                var manifest = JsonUtility.FromJson<ArtAssetManifest>(File.ReadAllText(manifestPath));
                Assert.That(manifest.export?.materials, Is.Not.Null.And.Not.Empty, manifestPath);

                // Every material the model renders with has an approved record, and no record is stale.
                Assert.That(manifest.target, Does.StartWith(ClientProjectPrefix), manifestPath);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(
                    manifest.target.Substring(ClientProjectPrefix.Length));
                Assert.That(model, Is.Not.Null, manifestPath);
                Assert.That(
                    model.GetComponentsInChildren<Renderer>(true)
                        .SelectMany(renderer => renderer.sharedMaterials)
                        .Select(material => material.name)
                        .Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal),
                    Is.EqualTo(manifest.export.materials
                        .Select(record => record.name)
                        .OrderBy(name => name, StringComparer.Ordinal)),
                    manifestPath);
                foreach (var record in manifest.export.materials)
                {
                    var where = manifestPath + " " + record.name;
                    var material = ArtModelImportRules.FindProjectMaterial(record.name);
                    Assert.That(material, Is.Not.Null, where);
                    Assert.That(ColorUtility.TryParseHtmlString(record.baseColor, out var approved), Is.True, where);
                    var actual = material.GetColor("_BaseColor");
                    Assert.That(actual.r, Is.EqualTo(approved.r).Within(1.5f / 255f), where);
                    Assert.That(actual.g, Is.EqualTo(approved.g).Within(1.5f / 255f), where);
                    Assert.That(actual.b, Is.EqualTo(approved.b).Within(1.5f / 255f), where);
                    Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(record.metallic).Within(0.01f), where);
                    Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(record.smoothness).Within(0.01f), where);
                }
            }
        }

        [Test]
        public void EveryArtModelKeepsItsRootsAtTheOriginWithoutCorrections()
        {
            foreach (var path in ModelPaths())
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                // Blender root objects sit at the origin unrotated, so a baked axis conversion
                // leaves identity transforms; a -90 degree X rotation means the rules did not apply.
                foreach (var transform in new[] { model.transform }
                             .Concat(model.transform.Cast<Transform>()))
                {
                    var where = path + " " + transform.name;
                    Assert.That(transform.localPosition.magnitude, Is.LessThan(0.0001f), where);
                    Assert.That(
                        Quaternion.Angle(transform.localRotation, Quaternion.identity),
                        Is.LessThan(0.01f),
                        where);
                    Assert.That((transform.localScale - Vector3.one).magnitude, Is.LessThan(0.0001f), where);
                }

                Assert.That(model.GetComponentsInChildren<Collider>(true), Is.Empty, path);
                Assert.That(model.GetComponentsInChildren<Camera>(true), Is.Empty, path);
                Assert.That(model.GetComponentsInChildren<Light>(true), Is.Empty, path);
            }
        }

        // The part of art/<Category>/<Name>/asset.json this test reads.
        [Serializable]
        private sealed class ArtAssetManifest
        {
            public string target = string.Empty;
            public ArtAssetExport export = new ArtAssetExport();
        }

        [Serializable]
        private sealed class ArtAssetExport
        {
            public ArtMaterialRecord[] materials = Array.Empty<ArtMaterialRecord>();
        }

        [Serializable]
        private sealed class ArtMaterialRecord
        {
            public string name = string.Empty;
            public string baseColor = string.Empty;
            public float metallic = -1f;
            public float smoothness = -1f;
        }
    }
}
