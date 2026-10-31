using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Characters;
using Mmorpg.WorldData.Content;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // A character's look as the server stores and sends it: the appearance content that both the
    // server and the bodies' catalogs name, the look a body shows from the choices, the garments of
    // the visible items with the headwear, the hood button, the creation rows, and the keys of the
    // corpse bodies per look. See docs/EQUIPMENT_VISUALS_DESIGN.md, Items.
    public sealed class CharacterLookTests
    {
        private const string BodyModelsPath = "Assets/Content/Player/PlayerBodyModels.asset";
        private const string MaleCatalogPath = "Assets/Art/Appearance/Human_Male_Appearance.asset";

        [Test]
        public void TheAppearanceContentOffersExactlyWhatEveryBodysCatalogHas()
        {
            var content = LoadContent();
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);

            Assert.That(content.BodyModels, Is.EquivalentTo(models.Models.Select(model => model.Name)));
            Assert.That(content.DefaultBodyModel, Is.EqualTo(models.Default.Name));
            foreach (var bodyModel in content.BodyModels)
            {
                var catalog = models.NamedOrDefault(bodyModel).Body.Catalog;
                var offers = content.OffersFor(bodyModel);
                Assert.That(Options(offers, CharacterAppearanceFormat.Face, string.Empty),
                    Is.EqualTo(catalog.SlotNamed(CharacterLooks.FaceSlot).Options.Select(option => option.Name)), bodyModel + " faces");
                Assert.That(Options(offers, CharacterAppearanceFormat.Eyes, string.Empty),
                    Is.EqualTo(catalog.SlotNamed(CharacterLooks.EyeSlot).Options.Select(option => option.Name)), bodyModel + " eyes");
                Assert.That(Options(offers, CharacterAppearanceFormat.Scar, string.Empty),
                    Is.EquivalentTo(Paints(catalog, AppearanceCategory.Scar, string.Empty)), bodyModel + " scars");
                Assert.That(Options(offers, CharacterAppearanceFormat.Tattoo, string.Empty),
                    Is.EquivalentTo(Paints(catalog, AppearanceCategory.Tattoo, string.Empty)), bodyModel + " tattoos");
                var underwearSlots = catalog.Paints
                    .Where(paint => paint.Category == AppearanceCategory.Underwear)
                    .Select(paint => paint.Slot)
                    .Distinct()
                    .ToArray();
                Assert.That(
                    offers.Where(offer => offer.Kind == CharacterAppearanceFormat.Underwear).Select(offer => offer.Slot),
                    Is.EquivalentTo(underwearSlots), bodyModel + " underwear slots");
                foreach (var slot in underwearSlots)
                {
                    Assert.That(Options(offers, CharacterAppearanceFormat.Underwear, slot),
                        Is.EquivalentTo(Paints(catalog, AppearanceCategory.Underwear, slot)), bodyModel + " underwear in " + slot);
                }

                Assert.That(Options(offers, CharacterAppearanceFormat.Hair, string.Empty),
                    Is.EquivalentTo(catalog.Garments
                        .Where(garment => garment.Slot == BodyAppearanceCatalog.Garment.HairSlot)
                        .Select(garment => garment.Name)), bodyModel + " hair");
            }
        }

        [Test]
        public void TheContentRulesOfEveryKindAreTheClientsRules()
        {
            var content = LoadContent();
            var rules = new Dictionary<string, AppearanceRules.Rule>
            {
                [CharacterAppearanceFormat.Face] = AppearanceRules.For(AppearanceCategory.Face),
                [CharacterAppearanceFormat.Eyes] = AppearanceRules.For(AppearanceCategory.Eye),
                [CharacterAppearanceFormat.Underwear] = AppearanceRules.For(AppearanceCategory.Underwear),
                [CharacterAppearanceFormat.Scar] = AppearanceRules.For(AppearanceCategory.Scar),
                [CharacterAppearanceFormat.Tattoo] = AppearanceRules.For(AppearanceCategory.Tattoo),
                [CharacterAppearanceFormat.Hair] = AppearanceRules.Hair,
            };

            Assert.That(content.Kinds.Select(kind => kind.Id), Is.EquivalentTo(rules.Keys));
            foreach (var kind in content.Kinds)
            {
                Assert.That(kind.Required, Is.EqualTo(rules[kind.Id].Required), kind.Id);
                Assert.That(kind.ChangesInGame, Is.EqualTo(rules[kind.Id].ChangesInGame), kind.Id);
            }
        }

        [Test]
        public void ALookTravelsAsChoicesAndBack()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(MaleCatalogPath);
            var look = BodyLook.Default
                .With(CharacterLooks.FaceSlot, "Clean")
                .With(CharacterLooks.EyeSlot, "Brown")
                .WithPaint("Briefs", true)
                .WithPaint("ForearmRunes", true)
                .WithGarment("DarkBrownHair", true);

            var choices = CharacterLooks.ChoicesOf(look.WithGarment("WoolCloak", true).WithPaint("Bra", true), catalog);

            // An item's garment and the other body's paint are not part of what the server stores.
            Assert.That(
                choices.Select(choice => choice.Kind + ":" + choice.Slot + ":" + choice.Option),
                Is.EquivalentTo(new[] { "face::Clean", "eyes::Brown", "underwear:legs:Briefs", "tattoo::ForearmRunes", "hair::DarkBrownHair" }));
            Assert.That(CharacterLooks.LookOf(choices), Is.EqualTo(look));
            Assert.That(CharacterLooks.LookOf(null), Is.EqualTo(BodyLook.Default));
            Assert.That(CharacterLooks.ChoicesOf(look, null), Is.Empty);
        }

        [Test]
        public void OthersSeeTheCharacterInItsVisibleItemsWithItsHeadwearAsItWearsIt()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(MaleCatalogPath);
            WithBundledCatalog(items =>
            {
                var hood = Lowerable(items);
                Assert.That(items.Presentation.TryGet(hood, out var entry), Is.True);
                var raised = Appearance(false, new RealtimeVisibleEquipment("head", hood));

                var up = RemotePlayerView.DressedLook(raised, catalog, items.Presentation);
                var down = RemotePlayerView.DressedLook(Appearance(true, new RealtimeVisibleEquipment("head", hood)), catalog, items.Presentation);

                Assert.That(up.HasGarment(entry.garments[0]), Is.True);
                Assert.That(down.HasGarment(entry.loweredGarments[0]), Is.True);
                Assert.That(down.HasGarment(entry.garments[0]), Is.False);
                Assert.That(up.HasGarment("DarkBrownHair") && down.HasGarment("DarkBrownHair"), Is.True);
                Assert.That(up.OptionFor(CharacterLooks.FaceSlot), Is.EqualTo("Clean"));
                Assert.That(RemotePlayerView.DressedLook(null, catalog, items.Presentation), Is.EqualTo(BodyLook.Default));
            });
        }

        [Test]
        public void TheInventoryOffersToLowerOnlyWornHeadwearWithLoweredGarments()
        {
            WithBundledCatalog(items =>
            {
                var hood = Item(Lowerable(items));
                var helmet = items.Definitions.First(definition =>
                    definition.EquipmentSlots.Contains("head")
                    && (!items.Presentation.TryGet(definition.Id, out var entry)
                        || entry.loweredGarments == null
                        || entry.loweredGarments.Length == 0));

                Assert.That(
                    InventorySlotPresentation.LowerableHeadwear(
                        new[] { new InventoryEquipmentSlot("body", 1, null), new InventoryEquipmentSlot("head", 0, hood) },
                        items),
                    Is.SameAs(hood));
                Assert.That(
                    InventorySlotPresentation.LowerableHeadwear(new[] { new InventoryEquipmentSlot("head", 0, Item(helmet.Id)) }, items),
                    Is.Null);
                Assert.That(InventorySlotPresentation.LowerableHeadwear(null, items), Is.Null);
            });
        }

        [Test]
        public void CreationStartsFromTheDefaultsOfEachBodyAndStepsThroughItsOffers()
        {
            var look = new CharacterCreationLook(new CharacterAppearanceCatalogResponse
            {
                kinds = new[]
                {
                    new CharacterAppearanceKindResponse { id = "face", required = true },
                    new CharacterAppearanceKindResponse { id = "hair", required = false, changesInGame = true },
                },
                bodyModels = new[]
                {
                    Body("Human_Male", Offer("face", "Stubble", "Clean"), Offer("hair", "DarkBrownHair")),
                    Body("Human_Female", Offer("face", "Blush", "Freckles")),
                },
            });

            Assert.That(look.IsAvailable, Is.True);
            Assert.That(look.BodyModel, Is.EqualTo("Human_Male"));
            Assert.That(look.OptionLabel(0), Is.EqualTo("Stubble"));
            Assert.That(look.OptionLabel(1), Is.EqualTo("none"));

            look.StepOption(0, -1);
            look.StepOption(1, 1);
            Assert.That(Describe(look.ToRequest()), Is.EqualTo("Human_Male face=Clean hair=DarkBrownHair"));
            // An optional kind steps through none.
            look.StepOption(1, 1);
            Assert.That(look.OptionLabel(1), Is.EqualTo("none"));

            look.StepBodyModel(1);
            Assert.That(Describe(look.ToRequest()), Is.EqualTo("Human_Female face=Blush"));
            Assert.That(look.OfferKind(0), Is.EqualTo("face"));
            Assert.That(look.OfferSlot(0), Is.Empty);
            look.StepBodyModel(1);
            Assert.That(look.BodyModel, Is.EqualTo("Human_Male"));
            Assert.That(new CharacterCreationLook(null).IsAvailable, Is.False);
        }

        [Test]
        public void ACorpseBodyIsNamedByItsModelAndLook()
        {
            var cloaked = BodyLook.Default.WithGarment("WoolCloak", true);

            Assert.That(PlayerCorpseBodies.KeyOf("Human_Male", cloaked), Is.EqualTo("Human_Male|" + cloaked.Key));
            Assert.That(PlayerCorpseBodies.KeyOf("Human_Male", null), Is.EqualTo(PlayerCorpseBodies.KeyOf("Human_Male", BodyLook.Default)));
            Assert.That(PlayerCorpseBodies.KeyOf("Human_Female", cloaked), Is.Not.EqualTo(PlayerCorpseBodies.KeyOf("Human_Male", cloaked)));
        }

        private static CharacterAppearanceCatalog LoadContent()
        {
            var root = ContentLayout.FindRepositoryRoot(Application.dataPath);
            var json = File.ReadAllText(ContentLayout.CharacterAppearancePath(ContentLayout.ContentRoot(root)));
            return CharacterAppearanceCatalog.Create(JsonUtility.FromJson<CharacterAppearanceDocument>(json));
        }

        private static IEnumerable<string> Options(IReadOnlyList<CharacterAppearanceOffer> offers, string kind, string slot)
        {
            var offer = offers.FirstOrDefault(candidate => candidate.Kind == kind && candidate.Slot == slot);
            return offer != null ? offer.Options : Array.Empty<string>();
        }

        private static IEnumerable<string> Paints(BodyAppearanceCatalog catalog, AppearanceCategory category, string slot)
        {
            return catalog.Paints
                .Where(paint => paint.Category == category && (paint.Slot ?? string.Empty) == slot)
                .Select(paint => paint.Name);
        }

        private static void WithBundledCatalog(Action<ClientItemCatalog> test)
        {
            ItemPresentationCatalogLoader.ResetCache();
            var config = ClientConfig.Load(out AsyncOperationHandle<ClientConfig> handle);
            try
            {
                Assert.That(ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out var items, out var error), Is.True, error);
                test(items);
            }
            finally
            {
                ItemPresentationCatalogLoader.ResetCache();
                ClientAssets.Release(handle);
            }
        }

        // The first item of the bundled catalog whose presentation lowers, such as a hood.
        private static string Lowerable(ClientItemCatalog items)
        {
            var definition = items.Definitions.First(candidate =>
                items.Presentation.TryGet(candidate.Id, out var entry)
                && entry.loweredGarments != null
                && entry.loweredGarments.Length > 0);
            return definition.Id;
        }

        private static RealtimeCharacterAppearance Appearance(bool headwearLowered, params RealtimeVisibleEquipment[] equipment)
        {
            return Appearance(headwearLowered, null, equipment);
        }

        private static RealtimeCharacterAppearance Appearance(bool headwearLowered, string toolInUse, params RealtimeVisibleEquipment[] equipment)
        {
            return new RealtimeCharacterAppearance(
                "Human_Male",
                new[]
                {
                    new RealtimeAppearanceChoice("face", string.Empty, "Clean"),
                    new RealtimeAppearanceChoice("hair", string.Empty, "DarkBrownHair"),
                },
                equipment,
                headwearLowered,
                toolInUse);
        }

        private static InventoryItem Item(string definitionId)
        {
            return new InventoryItem(Guid.NewGuid(), definitionId, 1, 1, Array.Empty<InventoryPolicy>());
        }

        private static CharacterBodyModelResponse Body(string id, params CharacterAppearanceOfferResponse[] offers)
        {
            return new CharacterBodyModelResponse { id = id, offers = offers };
        }

        private static CharacterAppearanceOfferResponse Offer(string kind, params string[] options)
        {
            return new CharacterAppearanceOfferResponse { kind = kind, slot = string.Empty, options = options };
        }

        private static string Describe(CharacterAppearanceRequest request)
        {
            return request.bodyModel + string.Concat(request.choices.Select(choice => " " + choice.kind + "=" + choice.option));
        }
    }
}
