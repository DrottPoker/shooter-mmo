using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class InventoryPresentationTests
    {
        private static readonly string[] DesignSlotIds =
        {
            "head", "neck", "shoulders", "back", "body", "legs", "hands", "feet", "main_hand",
            "off_hand", "ammunition", "ring_1", "ring_2", "tool", "bag"
        };

        private ClientItemCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            ItemPresentationCatalogLoader.ResetCache();
            catalog = FixtureItemCatalog.Load();
        }

        [TearDown]
        public void TearDown()
        {
            ItemPresentationCatalogLoader.ResetCache();
        }

        [Test]
        public void PaperDollPutsTheSlotsAroundTheCharacterAndTheHandsBelow()
        {
            var arrangement = EquipmentPaperDollLayout.Arrange(DesignSlotIds);

            Assert.That(arrangement.Left, Is.EqualTo(new[] { "head", "shoulders", "body", "legs", "feet" }));
            Assert.That(arrangement.Right, Is.EqualTo(new[] { "neck", "hands", "ring_1", "ring_2", "bag" }));
            Assert.That(arrangement.BottomRows.Count, Is.EqualTo(1));
            Assert.That(arrangement.BottomRows[0], Is.EqualTo(new[] { "main_hand", "off_hand", "ammunition", "tool" }));
            // The cloak's Back slot is hidden; every other slot shows once.
            Assert.That(arrangement.All, Is.EquivalentTo(DesignSlotIds.Where(slotId => slotId != "back")));
        }

        [Test]
        public void PaperDollAppendsUnknownSlotsInRowsBelow()
        {
            var arrangement = EquipmentPaperDollLayout.Arrange(
                DesignSlotIds.Concat(new[] { "cape", "head", " ", null, "quiver", "trinket", "belt", "charm" }));

            Assert.That(arrangement.BottomRows.Count, Is.EqualTo(3));
            Assert.That(arrangement.BottomRows[1], Is.EqualTo(new[] { "cape", "quiver", "trinket", "belt" }));
            Assert.That(arrangement.BottomRows[2], Is.EqualTo(new[] { "charm" }));
            Assert.That(arrangement.All.Count(), Is.EqualTo(DesignSlotIds.Length - 1 + 5));

            // The fixture catalog lacks some design slots; its columns are shorter.
            var fixture = EquipmentPaperDollLayout.Arrange(catalog.Gameplay.EquipmentSlots.Select(slot => slot.Id));
            Assert.That(
                fixture.All,
                Is.EquivalentTo(catalog.Gameplay.EquipmentSlots.Select(slot => slot.Id).Where(slotId => slotId != "back")));
            var empty = EquipmentPaperDollLayout.Arrange(null);
            Assert.That(empty.All, Is.Empty);
            Assert.That(empty.BottomRows, Is.Empty);
        }

        [Test]
        public void SearchMatchesNameOrCategoryIgnoringCase()
        {
            Assert.That(InventorySearch.Matches(catalog, "fixture.ore", string.Empty), Is.True);
            Assert.That(InventorySearch.Matches(catalog, "fixture.ore", "  "), Is.True);
            Assert.That(InventorySearch.Matches(catalog, "fixture.ore", "ORE"), Is.True);
            Assert.That(InventorySearch.Matches(catalog, "fixture.ore", "materi"), Is.True);
            Assert.That(InventorySearch.Matches(catalog, "fixture.ore", "arrow"), Is.False);
            Assert.That(InventorySearch.Matches(null, "fixture.ore", "ore"), Is.False);
        }

        [Test]
        public void EveryItemHasTheCommonFrameUntilItemsHaveARarity()
        {
            var theme = UnityEngine.ScriptableObject.CreateInstance<UiTheme>();
            try
            {
                theme.RarityCommon = new UnityEngine.Color(0.9f, 0.9f, 0.9f, 1f);
                Assert.That(ItemRarityPresentation.FrameColor(theme, catalog, "fixture.sword"), Is.EqualTo(theme.RarityCommon));
                Assert.That(ItemRarityPresentation.FrameColor(theme, catalog, "fixture.ore"), Is.EqualTo(theme.RarityCommon));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void CarryMeterSpansToTheHardCapAndReportsTheLoadLevel()
        {
            var normal = CarryLoadPresentation.From(1_800, 2_500, 7_200, 10_000);
            Assert.That(normal.Level, Is.EqualTo(CarryLoadLevel.Normal));
            Assert.That(normal.FillFraction, Is.EqualTo(7_200f / 14_000f).Within(0.0001f));
            Assert.That(normal.FullCapacityMarkerFraction, Is.EqualTo(100f / 140f).Within(0.0001f));
            Assert.That(normal.WeightText, Is.EqualTo("1800 / 2500"));
            Assert.That(normal.LoadPercentText, Is.EqualTo("72%"));
            Assert.That(normal.SpeedPercentText, Is.EqualTo("100%"));

            Assert.That(
                CarryLoadPresentation.From(2_500, 2_500, 10_000, 10_000).Level,
                Is.EqualTo(CarryLoadLevel.Normal));
            var slowed = CarryLoadPresentation.From(3_000, 2_500, 12_000, 6_000);
            Assert.That(slowed.Level, Is.EqualTo(CarryLoadLevel.Slowed));
            Assert.That(slowed.SpeedPercentText, Is.EqualTo("60%"));
            var capped = CarryLoadPresentation.From(3_500, 2_500, 14_000, 2_000);
            Assert.That(capped.Level, Is.EqualTo(CarryLoadLevel.AtHardCap));
            Assert.That(capped.FillFraction, Is.EqualTo(1f));

            // An involuntary death overflow above the cap still fills the bar exactly once.
            var overflow = CarryLoadPresentation.From(4_000, 2_500, 16_000, 2_000);
            Assert.That(overflow.Level, Is.EqualTo(CarryLoadLevel.AtHardCap));
            Assert.That(overflow.FillFraction, Is.EqualTo(1f));

            // The floored ratio of an exact hard cap can stay below 140 percent.
            var flooredCap = CarryLoadPresentation.From(2_801, 2_001, 13_998, 2_000);
            Assert.That(flooredCap.Level, Is.EqualTo(CarryLoadLevel.AtHardCap));
            Assert.That(flooredCap.FillFraction, Is.EqualTo(1f));
            Assert.That(
                CarryLoadPresentation.From(2_800, 2_001, 13_993, 2_010).Level,
                Is.EqualTo(CarryLoadLevel.Slowed));
            Assert.That(
                CarryLoadPresentation.From(0, 0, 14_000, 2_000).Level,
                Is.EqualTo(CarryLoadLevel.AtHardCap));
            Assert.That(
                CarryLoadPresentation.From(10, 2_000, 50, 10_000).LoadPercentText,
                Is.EqualTo("0.5%"));
        }

        [Test]
        public void BlockedEquipmentSlotsNameTheItemThatBlocksThem()
        {
            var bow = CreateItem("fixture.bow", 1);
            var equipment = new[]
            {
                new InventoryEquipmentSlot("main_hand", 2, bow),
                new InventoryEquipmentSlot("off_hand", 3, null),
                new InventoryEquipmentSlot("head", 0, null)
            };

            var blocked = InventorySlotPresentation.FindBlockedEquipmentSlots(catalog, equipment);

            Assert.That(blocked.Keys, Is.EquivalentTo(new[] { "off_hand" }));
            Assert.That(blocked["off_hand"].SlotId, Is.EqualTo("main_hand"));
            Assert.That(blocked["off_hand"].Item, Is.SameAs(bow));

            var swordOnly = new[]
            {
                new InventoryEquipmentSlot("main_hand", 2, CreateItem("fixture.sword", 1)),
                new InventoryEquipmentSlot("off_hand", 3, null)
            };
            Assert.That(InventorySlotPresentation.FindBlockedEquipmentSlots(catalog, swordOnly), Is.Empty);

            // An occupied slot is drawn with its item, never as blocked.
            var occupied = new[]
            {
                new InventoryEquipmentSlot("main_hand", 2, bow),
                new InventoryEquipmentSlot("off_hand", 3, CreateItem("fixture.shield", 1))
            };
            Assert.That(InventorySlotPresentation.FindBlockedEquipmentSlots(catalog, occupied), Is.Empty);
        }

        [Test]
        public void SlotLabelsUseCatalogDisplayNames()
        {
            Assert.That(
                InventorySlotPresentation.AcceptedTagsLabel(catalog, new[] { "potion", "material" }),
                Is.EqualTo("Potion/Material"));
            Assert.That(
                InventorySlotPresentation.AcceptedTagsLabel(catalog, new[] { "unknown_tag" }),
                Is.EqualTo("unknown_tag"));
            Assert.That(
                InventorySlotPresentation.AcceptedTagsLabel(catalog, Array.Empty<string>()),
                Is.Empty);
            Assert.That(
                InventorySlotPresentation.EquipmentSlotName(catalog, "main_hand"),
                Is.EqualTo("Main Hand"));
            Assert.That(
                InventorySlotPresentation.EquipmentSlotName(catalog, "neck"),
                Is.EqualTo("neck"));
            Assert.That(InventorySlotPresentation.QuantityText(1), Is.Empty);
            Assert.That(InventorySlotPresentation.QuantityText(480), Is.EqualTo("480"));
            Assert.That(InventorySlotPresentation.QuantityText(1000), Is.EqualTo("1000"));
        }

        [Test]
        public void ItemTooltipShowsQuantityWeightSlotsAndPolicies()
        {
            var arrows = InventorySlotPresentation.BuildItemTooltip(
                catalog,
                CreateItem("fixture.arrow", 30),
                "Equipped in Ammunition");
            Assert.That(arrows, Does.Contain("<b>Fixture Arrow</b>"));
            Assert.That(
                arrows,
                Does.Contain(Ui("ui/inventory/tooltip-category-quantity", ("category", "Ammunition"), ("quantity", "30"), ("maximum", "60"))));
            Assert.That(arrows, Does.Contain(Ui("ui/inventory/tooltip-weight-each", ("weight", "30"), ("unit", "1"))));
            Assert.That(arrows, Does.Contain(Ui("ui/inventory/tooltip-equips", ("slots", "Ammunition"))));
            Assert.That(arrows, Does.EndWith("Equipped in Ammunition"));
            Assert.That(arrows, Does.Not.Contain(Ui("ui/inventory/tooltip-indestructible")));

            var bow = InventorySlotPresentation.BuildItemTooltip(
                catalog,
                CreateItem("fixture.bow", 1),
                string.Empty);
            Assert.That(
                bow,
                Does.Contain(Ui("ui/inventory/tooltip-equips-blocks", ("slots", "Main Hand"), ("blocked", "Off Hand"))));
            Assert.That(bow, Does.Not.Contain("Not allowed"));
            Assert.That(bow, Does.Contain(Ui("ui/inventory/tooltip-weight", ("weight", "25"))));
            Assert.That(bow, Does.Not.Contain("each"));

            var letter = new InventoryItem(
                Guid.NewGuid(),
                "fixture.letter",
                1,
                1,
                new[]
                {
                    new InventoryPolicy("protected_on_death", "active", "catalog_default"),
                    new InventoryPolicy("insured", "consumed", "insurance_npc")
                });
            var letterTooltip = InventorySlotPresentation.BuildItemTooltip(catalog, letter, null);
            Assert.That(
                letterTooltip,
                Does.Contain(Ui(
                    "ui/inventory/tooltip-policy",
                    ("policy", Ui("ui/inventory/policy-protected-on-death")),
                    ("source", Ui("ui/inventory/protection-catalog-rule")))));
            Assert.That(letterTooltip, Does.Not.Contain(Ui("ui/inventory/policy-insured")));
            Assert.That(letterTooltip, Does.Contain(Ui("ui/inventory/tooltip-indestructible")));
            Assert.That(InventorySlotPresentation.PolicyBadge(letter), Is.EqualTo("P"));
            Assert.That(
                InventorySlotPresentation.PolicyBadge(new InventoryItem(
                    Guid.NewGuid(),
                    "fixture.ring",
                    1,
                    1,
                    new[]
                    {
                        new InventoryPolicy("protected_on_death", "active", "quest_grant"),
                        new InventoryPolicy("insured", "active", "insurance_npc")
                    })),
                Is.EqualTo("PI"));
            Assert.That(InventorySlotPresentation.PolicyBadge(CreateItem("fixture.ore", 5)), Is.Empty);
            Assert.That(
                InventorySlotPresentation.BuildItemTooltip(catalog, CreateItem("fixture.missing", 2), "Bank slot 1"),
                Is.EqualTo("<b>fixture.missing</b>\n" + Ui("ui/inventory/tooltip-quantity", ("quantity", "2")) + "\nBank slot 1"));
        }

        // A text of the checked-in ui table with its named values, as the player reads it.
        private static string Ui(string key, params (string Name, string Value)[] values)
        {
            return FixtureTexts.Ui().Format(
                key,
                values.Select(value => MessageArgument.Text(value.Name, value.Value)).ToArray());
        }

        private static InventoryItem CreateItem(string definitionId, int quantity)
        {
            return new InventoryItem(
                Guid.NewGuid(),
                definitionId,
                quantity,
                1,
                Array.Empty<InventoryPolicy>());
        }
    }
}
