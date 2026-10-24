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

                // The parts and the garments of the body, as the exports give them.
                Assert.That(
                    ArtAppearanceCatalogs.Matches(catalog, ArtAppearanceCatalogs.PartsFor(manifest), ArtAppearanceCatalogs.GarmentsFor(manifest)),
                    Is.True,
                    path + " has other parts or garments than the exports give; run " + ArtAppearanceCatalogs.MenuPath);
            }

            Assert.That(
                AssetDatabase.FindAssets("t:BodyAppearanceCatalog", new[] { ArtAppearanceCatalogs.Folder })
                    .Select(AssetDatabase.GUIDToAssetPath),
                Is.EquivalentTo(manifests.Select(ArtAppearanceCatalogs.PathFor)));
        }

        [Test]
        public void EveryFaceOfAnEditableBodyIsDrawnByItsPartsOnce()
        {
            // Every triangle of a part comes from one face of the editable mesh, and every face is
            // drawn by as many triangles as it has corners less two, so garments can hide any face.
            var bodies = ArtAppearanceCatalogs.ManifestsWithLooks().Where(manifest => manifest.UnityDataPath != null).ToArray();
            Assert.That(bodies, Is.Not.Empty);
            foreach (var body in bodies)
            {
                // A new import of the topology alone updates the appearance catalogs.
                Assert.That(ArtModelImportRules.AppliesToData(body.UnityDataPath), Is.True, body.path);
                var topology = ArtAppearanceCatalogs.TopologyFor(body);
                var parts = ArtAppearanceCatalogs.PartsFor(body);
                Assert.That(parts.Length, Is.EqualTo(10), body.path);
                var triangles = new int[topology.FaceCount];
                foreach (var part in parts)
                {
                    foreach (var face in part.TriangleFaces)
                    {
                        triangles[face]++;
                    }

                    Assert.That(part.VertexSources.All(vertex => vertex >= 0 && vertex < topology.VertexCount), Is.True, part.Name);
                }

                for (var face = 0; face < triangles.Length; face++)
                {
                    Assert.That(triangles[face], Is.EqualTo(topology.SizeOf(face) - 2), body.path + " face " + face);
                }
            }
        }

        [Test]
        public void EveryGarmentOfABodyHasItsDataForEveryTriangle()
        {
            var ownMasks = ArtAssetManifests.Textures().Where(texture => texture.export.masks.Length > 0)
                .Select(texture => texture.Name).ToHashSet();
            var coverNothing = ArtAssetManifests.Garments().Where(template => !template.covers)
                .Select(template => template.Name).ToHashSet();
            foreach (var body in ArtAppearanceCatalogs.ManifestsWithLooks())
            {
                foreach (var garment in ArtAppearanceCatalogs.GarmentsFor(body))
                {
                    var where = body.path + " " + garment.Name;
                    var renderer = garment.Model.GetComponentInChildren<SkinnedMeshRenderer>();
                    Assert.That(garment.Underlay.Count, Is.EqualTo(renderer.sharedMesh.triangles.Length / 3), where);
                    // A texture with masks of its own, such as a net, may leave every body face visible,
                    // and a template that covers nothing, such as the cloak or the hood down, does.
                    if (!ownMasks.Contains(garment.Name) && !coverNothing.Contains(garment.Template))
                    {
                        Assert.That(garment.Covered, Is.Not.Empty, where);
                    }

                    Assert.That(garment.Slot, Is.Not.Empty, where);
                    // A garment is a texture on its template, and draws with the texture's material.
                    Assert.That(garment.Template, Is.Not.Empty, where);
                    Assert.That(garment.Material, Is.Not.Null, where);
                    Assert.That(garment.Material.name, Is.EqualTo("M_Garment_" + garment.Name), where);
                    Assert.That(garment.Material.shader.name, Is.EqualTo(ArtMaterialCreator.GarmentShader), where);
                }
            }
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
            Assert.That(catalog.SlotNamed("Face").Options.Select(option => option.Name), Is.EqualTo(new[] { "Stubble", "Clean" }));
            // A scar under the tattoos, and the briefs over them; the man has no bra.
            Assert.That(catalog.Paints.Select(paint => paint.Name), Is.EqualTo(new[] { "Scar", "Tattoo", "ForearmRunes", "Briefs" }));
            Assert.That(catalog.Paints.Select(paint => paint.Category),
                Is.EqualTo(new[] { AppearanceCategory.Scar, AppearanceCategory.Tattoo, AppearanceCategory.Tattoo, AppearanceCategory.Underwear }));
            // Every image lies at its crop of the paint UVs, and the runes on the left forearm only in
            // the left half, which holds the body's left side.
            foreach (var paint in catalog.Paints)
            {
                Assert.That(paint.Rect.x >= 0f && paint.Rect.y >= 0f && paint.Rect.z > 0f && paint.Rect.w > 0f
                    && paint.Rect.x + paint.Rect.z <= 1f && paint.Rect.y + paint.Rect.w <= 1f, Is.True, paint.Name + " " + paint.Rect);
            }

            var runes = catalog.Paints.Single(paint => paint.Name == "ForearmRunes").Rect;
            Assert.That(runes.x + runes.z, Is.LessThanOrEqualTo(0.5f));
            Assert.That(catalog.Paints.Single(paint => paint.Name == "Briefs").Slot, Is.EqualTo("legs"));
            Assert.That(catalog.Paints.Single(paint => paint.Name == "Scar").Material.name, Is.EqualTo("M_Face_Male"));
            // The garments from the lowest layer up, and by name within a layer.
            Assert.That(catalog.Garments.Select(garment => garment.Name),
                Is.EqualTo(new[]
                {
                    "WoolTrousers", "LeatherBoots", "LeatherJerkin", "LinenLongShirt", "LinenShirt", "NetShirt", "WoolRobe",
                    "LeatherGloves", "WoolCloak", "DarkBrownHair", "IronNasalHelm", "WoolHoodDown", "WoolHoodUp",
                }));
            Assert.That(catalog.GarmentNamed("WoolTrousers").Template, Is.EqualTo("Trousers"));
            Assert.That(catalog.GarmentNamed("LeatherJerkin").Template, Is.EqualTo("Shirt"));
            Assert.That(catalog.GarmentNamed("LinenLongShirt").Template, Is.EqualTo("LongShirt"));
            // Boots over the trouser legs and gloves over every sleeve, each in a slot of its own.
            Assert.That(catalog.GarmentNamed("LeatherBoots").Template, Is.EqualTo("Boots"));
            Assert.That(catalog.GarmentNamed("LeatherBoots").Slot, Is.EqualTo("feet"));
            Assert.That(catalog.GarmentNamed("LeatherGloves").Template, Is.EqualTo("Gloves"));
            Assert.That(catalog.GarmentNamed("LeatherGloves").Slot, Is.EqualTo("hands"));
            // A robe whose skirt hides the trousers, and a cloak on the back that lies on top and
            // hides nothing, both on cloth chains.
            Assert.That(catalog.GarmentNamed("WoolRobe").Template, Is.EqualTo("Robe"));
            Assert.That(catalog.GarmentNamed("WoolRobe").Hides, Is.EqualTo(new[] { "legs" }));
            Assert.That(catalog.GarmentNamed("WoolRobe").Cloth, Is.EqualTo("robe"));
            Assert.That(catalog.GarmentNamed("WoolCloak").Template, Is.EqualTo("Cloak"));
            Assert.That(catalog.GarmentNamed("WoolCloak").Slot, Is.EqualTo("back"));
            Assert.That(catalog.GarmentNamed("WoolCloak").Cloth, Is.EqualTo("cloak"));
            Assert.That(catalog.GarmentNamed("WoolCloak").Covered, Is.Empty);
            // The hair over the cloak, and headwear over the hair: the hood up and the helmet press
            // it flat, the hood down leaves it as it is.
            Assert.That(catalog.GarmentNamed("DarkBrownHair").Template, Is.EqualTo("ShoulderHair"));
            Assert.That(catalog.GarmentNamed("DarkBrownHair").Slot, Is.EqualTo("hair"));
            Assert.That(catalog.GarmentNamed("WoolHoodUp").Template, Is.EqualTo("HoodUp"));
            Assert.That(catalog.GarmentNamed("WoolHoodUp").Slot, Is.EqualTo("head"));
            Assert.That(catalog.GarmentNamed("WoolHoodUp").Hair, Is.EqualTo("compress"));
            Assert.That(catalog.GarmentNamed("WoolHoodDown").Template, Is.EqualTo("HoodDown"));
            Assert.That(catalog.GarmentNamed("WoolHoodDown").Hair, Is.EqualTo("show"));
            Assert.That(catalog.GarmentNamed("IronNasalHelm").Template, Is.EqualTo("NasalHelm"));
            Assert.That(catalog.GarmentNamed("IronNasalHelm").Hair, Is.EqualTo("compress"));
        }
    }
}
