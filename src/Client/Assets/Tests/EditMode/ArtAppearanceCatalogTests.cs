using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The appearance catalogs come from the variants that asset.json names, through
    // MMORPG > Tools > Art Pipeline > Update Appearance Catalogs; see docs/ART_PIPELINE.md.
    public sealed class ArtAppearanceCatalogTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        [Test]
        public void EveryCatalogIsWhatTheExportedVariantsGive()
        {
            var manifests = ArtAppearanceCatalogs.ManifestsWithLooks();
            Assert.That(manifests, Is.Not.Empty);
            foreach (var manifest in manifests)
            {
                var path = ArtAppearanceCatalogs.PathFor(manifest);
                var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(path);
                Assert.That(catalog, Is.Not.Null, path + " is missing; run " + ArtAppearanceCatalogs.MenuPath);
                var expected = ArtAppearanceCatalogs.SlotsFor(manifest);
                Assert.That(catalog.Slots.Select(slot => slot.Name), Is.EqualTo(expected.Select(slot => slot.Name)), path);
                foreach (var slot in expected)
                {
                    var actual = catalog.SlotNamed(slot.Name);
                    var where = path + " " + slot.Name;
                    Assert.That(actual.Material, Is.Not.Null, where);
                    Assert.That(actual.Material, Is.SameAs(slot.Material), where);
                    Assert.That(actual.Options.Select(option => option.Name), Is.EqualTo(slot.Options.Select(option => option.Name)), where);
                    Assert.That(actual.Options.Select(option => option.Texture), Is.EqualTo(slot.Options.Select(option => option.Texture)), where);
                    Assert.That(actual.Options.All(option => option.Texture != null), Is.True, where);
                    // The first option is the material's own image.
                    Assert.That(actual.Options[0].Texture, Is.SameAs(actual.Material.GetTexture("_BaseMap")), where);
                }

                var paints = ArtAppearanceCatalogs.PaintsFor(manifest);
                Assert.That(catalog.Paints.Select(paint => paint.Name), Is.EqualTo(paints.Select(paint => paint.Name)), path);
                for (var index = 0; index < paints.Length; index++)
                {
                    var actual = catalog.Paints[index];
                    var where = path + " paint " + actual.Name;
                    Assert.That(actual.Material, Is.Not.Null, where);
                    Assert.That(actual.Material, Is.SameAs(paints[index].Material), where);
                    Assert.That(actual.Texture, Is.Not.Null, where);
                    Assert.That(actual.Texture, Is.SameAs(paints[index].Texture), where);
                    Assert.That(actual.Layer, Is.EqualTo(paints[index].Layer), where);
                    Assert.That(actual.Smoothness, Is.EqualTo(paints[index].Smoothness), where);
                    Assert.That(actual.Metallic, Is.EqualTo(paints[index].Metallic), where);
                }
            }

            Assert.That(
                AssetDatabase.FindAssets("t:BodyAppearanceCatalog", new[] { ArtAppearanceCatalogs.Folder })
                    .Select(AssetDatabase.GUIDToAssetPath),
                Is.EquivalentTo(manifests.Select(ArtAppearanceCatalogs.PathFor)));
        }

        [Test]
        public void EveryPaintedMaterialDrawsWithTheSkinShader()
        {
            var painted = ArtAssetManifests.PaintedMaterialNames();
            Assert.That(painted, Is.Not.Empty);
            foreach (var name in painted)
            {
                var material = ArtModelImportRules.FindProjectMaterial(name);
                Assert.That(material, Is.Not.Null, name);
                Assert.That(material.shader.name, Is.EqualTo(ArtMaterialCreator.SkinShader), name);
            }
        }

        [Test]
        public void TheBodyOffersTheLooksOfItsModel()
        {
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            var catalog = body.GetComponent<PlayerBodyAppearance>().Catalog;
            Assert.That(catalog, Is.Not.Null);
            var manifest = ArtAppearanceCatalogs.ManifestsWithLooks()
                .Single(candidate => candidate.UnityTargetPath == "Assets/Art/Models/Characters/Human_Male.fbx");
            Assert.That(AssetDatabase.GetAssetPath(catalog), Is.EqualTo(ArtAppearanceCatalogs.PathFor(manifest)));
            Assert.That(catalog.Slots.Select(slot => slot.Name), Is.EquivalentTo(new[] { "Eye", "Face" }));
            Assert.That(catalog.Paints.Select(paint => paint.Name), Is.EquivalentTo(new[] { "LeatherJerkin" }));
        }
    }
}
