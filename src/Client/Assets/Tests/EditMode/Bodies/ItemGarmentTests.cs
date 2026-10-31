using System;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // Items show their garments as the item presentation names them, in their dye colors, as
    // docs/EQUIPMENT_VISUALS_DESIGN.md describes for delivery step 6.
    public sealed class ItemGarmentTests
    {
        private static readonly string[] BodyCatalogPaths =
        {
            "Assets/Art/Appearance/Human_Male_Appearance.asset",
            "Assets/Art/Appearance/Human_Female_Appearance.asset",
        };

        // The equipment slots whose items show garments: those with a visual slot of the same name.
        private static readonly string[] VisualSlots = { "head", "body", "legs", "hands", "feet", "back" };

        private ClientConfig config;
        private AsyncOperationHandle<ClientConfig> configHandle;
        private ClientItemCatalog bundled;

        [SetUp]
        public void SetUp()
        {
            ItemPresentationCatalogLoader.ResetCache();
            config = ClientConfig.Load(out configHandle);
            Assert.That(ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out bundled, out var error), Is.True, error);
        }

        [TearDown]
        public void TearDown()
        {
            ItemPresentationCatalogLoader.ResetCache();
            ClientAssets.Release(configHandle);
            configHandle = default;
        }

        [Test]
        public void EveryWearableItemShowsGarmentsThatFitEveryBody()
        {
            var catalogs = BodyCatalogPaths.Select(AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>).ToArray();
            Assert.That(catalogs, Has.None.Null);
            foreach (var definition in bundled.Definitions)
            {
                Assert.That(bundled.Presentation.TryGet(definition.Id, out var entry), Is.True, definition.Id);
                var wearable = definition.EquipmentSlots.Any(slot => VisualSlots.Contains(slot));
                Assert.That(entry.garments, wearable ? Is.Not.Empty : Is.Empty,
                    definition.Id + (wearable ? " is worn in a visual slot but shows no garment" : " shows garments but is worn in no visual slot"));
                foreach (var catalog in catalogs)
                {
                    var where = definition.Id + " on " + catalog.name;
                    foreach (var name in entry.garments.Concat(entry.loweredGarments))
                    {
                        var garment = catalog.GarmentNamed(name);
                        Assert.That(garment, Is.Not.Null, where + " names the garment " + name + ", which the body's catalog lacks");
                        Assert.That(definition.EquipmentSlots, Does.Contain(garment.Slot), where + ": " + name);
                        // Its dye colors tint channels the garment's texture has.
                        Assert.That(entry.dyes.Length, Is.LessThanOrEqualTo(Mathf.RoundToInt(garment.Material.GetFloat("_DyeCount"))),
                            where + ": " + name + " has fewer dye channels than the item has colors");
                    }

                    // A lowered garment takes the place of one in the same visual slot.
                    Assert.That(
                        entry.loweredGarments.Select(name => catalog.GarmentNamed(name).Slot).Distinct(),
                        Is.SubsetOf(entry.garments.Select(name => catalog.GarmentNamed(name).Slot)),
                        where);
                }
            }
        }

        [Test]
        public void EquipmentDressesTheLookTheCharacterChose()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]);
            var own = BodyLook.Default.With("Face", "Clean").WithPaint("Tattoo", true)
                .WithGarment("DarkBrownHair", true).WithGarment("LeatherJerkin", true);
            var equipment = new[]
            {
                new EquippedItem("head", "clothing.blue_wool_hood"),
                new EquippedItem("back", "clothing.red_wool_cloak"),
                new EquippedItem("body", "clothing.wool_robe"),
                new EquippedItem("feet", "armor.leather_boots"),
                new EquippedItem("ring_1", "jewelry.copper_ring"),
                new EquippedItem("main_hand", "weapon.training_shortbow"),
            };

            var dressed = EquipmentGarments.Dress(own, catalog, equipment, bundled.Presentation, false);

            // The hair and the face stay, a garment the look named but no item shows is taken off,
            // items without garments add none, and the bow is held in the left hand.
            Assert.That(dressed.Key, Is.EqualTo(
                "Attachment=TrainingShortbow;Face=Clean;Garment=DarkBrownHair+LeatherBoots+WoolCloak@742519+WoolHoodUp@3A5A85+WoolRobe;Paint=Tattoo"));
            Assert.That(dressed.DyesFor("WoolHoodUp").Single(), Is.EqualTo(Rgb(0x3A, 0x5A, 0x85)));
            Assert.That(dressed.DyesFor("WoolRobe"), Is.Empty);

            // Lowered, the hood shows its lowered garment in the same colors.
            var lowered = EquipmentGarments.Dress(own, catalog, equipment, bundled.Presentation, true);
            Assert.That(lowered.HasGarment("WoolHoodUp"), Is.False);
            Assert.That(lowered.DyesFor("WoolHoodDown").Single(), Is.EqualTo(Rgb(0x3A, 0x5A, 0x85)));

            // Without equipment or presentation the body wears only its hair.
            Assert.That(EquipmentGarments.Dress(own, catalog, null, bundled.Presentation, false).Garments,
                Is.EqualTo(new[] { "DarkBrownHair" }));
            Assert.That(EquipmentGarments.Dress(own, catalog, equipment, null, false).Garments,
                Is.EqualTo(new[] { "DarkBrownHair" }));
        }

        [Test]
        public void TheItemInTheSlotOfAGarmentWinsIt()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]);
            // A robe with its own hood in head, worn with a helmet in head, and a second robe whose
            // hood would take the slot too.
            var presentation = Presentation(
                Entry("test.hooded_robe", new[] { "WoolRobe", "WoolHoodUp" }),
                Entry("test.helmet", new[] { "IronNasalHelm" }),
                Entry("test.unknown", new[] { "NoSuchGarment" }));

            var withHelmet = EquipmentGarments.Dress(BodyLook.Default, catalog, new[]
            {
                new EquippedItem("body", "test.hooded_robe"),
                new EquippedItem("head", "test.helmet"),
            }, presentation, false);
            Assert.That(withHelmet.Garments, Is.EquivalentTo(new[] { "WoolRobe", "IronNasalHelm" }));

            var hoodOnly = EquipmentGarments.Dress(BodyLook.Default, catalog, new[]
            {
                new EquippedItem("body", "test.hooded_robe"),
                new EquippedItem("legs", "test.unknown"),
            }, presentation, false);
            Assert.That(hoodOnly.Garments, Is.EquivalentTo(new[] { "WoolRobe", "WoolHoodUp" }));
        }

        [Test]
        public void TheGarmentShaderTakesAnItemsDyeColors()
        {
            var cloak = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]).GarmentNamed("WoolCloak");
            var source = cloak.Material;
            Assert.That(source.GetFloat("_DyeCount"), Is.EqualTo(1f));
            Assert.That(source.GetTexture("_DyeMask"), Is.Not.Null);
            using (var materials = new PlayerBodyMaterials())
            {
                var blue = materials.For(source, null, Color.white, null, new[] { Rgb(0x2B, 0x43, 0x64) });
                Assert.That(blue, Is.Not.SameAs(source));
                Assert.That(ColorUtility.ToHtmlStringRGB(blue.GetColor("_DyeColor1")), Is.EqualTo("2B4364"));
                Assert.That(blue.GetTexture("_DyeMask"), Is.SameAs(source.GetTexture("_DyeMask")));
                // Bodies in the same colors share the copy, and its natural colors draw with its own.
                Assert.That(materials.For(source, null, Color.white, null, new[] { Rgb(0x2B, 0x43, 0x64) }), Is.SameAs(blue));
                Assert.That(materials.For(source, null, Color.white, null, new[] { source.GetColor("_DyeColor1") }), Is.SameAs(source));
                Assert.That(materials.For(source, null, Color.white, null, null), Is.SameAs(source));
                // Colors beyond the garment's channels are left out.
                var extra = materials.For(source, null, Color.white, null,
                    new[] { Rgb(0x2B, 0x43, 0x64), Rgb(0x74, 0x25, 0x19) });
                Assert.That(extra, Is.SameAs(blue));
                Assert.That(materials.Count, Is.EqualTo(1));
            }

            // An undyeable garment keeps its own material whatever an item asks.
            var boots = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(BodyCatalogPaths[0]).GarmentNamed("LeatherBoots");
            using (var materials = new PlayerBodyMaterials())
            {
                Assert.That(materials.For(boots.Material, null, Color.white, null, new[] { Rgb(0x2B, 0x43, 0x64) }),
                    Is.SameAs(boots.Material));
            }
        }

        [Test]
        public void ThePresentationCatalogRejectsBrokenGarmentsAndDyes()
        {
            var revision = new string('a', 64);
            var error = Assert.Throws<ItemPresentationCatalogValidationException>(() => ItemPresentationCatalogCompiler.Compile(
                revision,
                new[] { "test.a", "test.b", "test.c", "test.d" },
                new[]
                {
                    Entry("test.a", new[] { "Wool Robe" }),
                    Entry("test.b", new[] { "WoolRobe", "WoolRobe" }, dyes: new[] { "#3a5a85" }),
                    Entry("test.c", new string[0], new[] { "WoolHoodDown" }, new[] { "#3A5A85" }),
                    Entry("test.d", new[] { "WoolRobe" }, dyes: new[] { "#111111", "#222222", "#333333", "#444444" }),
                }));

            Assert.That(error.Errors, Is.EquivalentTo(new[]
            {
                "entries[0].garments must name garments with letters and digits.",
                "entries[1].garments names a garment twice.",
                "entries[1].dyes must hold colors as #RRGGBB with upper-case hexadecimal digits.",
                "entries[2] has lowered garments or dyes but shows no garments.",
                "entries[3].dyes lists more than 3 dye colors.",
            }));
        }

        private static ItemPresentationCatalogIndex Presentation(params ItemPresentationEntry[] entries)
        {
            var revision = new string('b', 64);
            var ids = entries.Select(entry => entry.definitionId).ToArray();
            var document = ItemPresentationCatalogCompiler.Compile(revision, ids, entries);
            Assert.That(ItemPresentationCatalogIndex.TryCreate(document, revision, ids, out var index, out var error), Is.True, error);
            return index;
        }

        private static ItemPresentationEntry Entry(string id, string[] garments, string[] lowered = null, string[] dyes = null)
        {
            return new ItemPresentationEntry
            {
                definitionId = id,
                iconAddress = string.Empty,
                localizationKey = "items." + id + ".name",
                fallbackDisplayName = id,
                prefabPresentationKey = string.Empty,
                garments = garments,
                loweredGarments = lowered ?? Array.Empty<string>(),
                dyes = dyes ?? Array.Empty<string>(),
            };
        }

        private static Color Rgb(int red, int green, int blue)
        {
            return new Color32((byte)red, (byte)green, (byte)blue, 255);
        }
    }
}
