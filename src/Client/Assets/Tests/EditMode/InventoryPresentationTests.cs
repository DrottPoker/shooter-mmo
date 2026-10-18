using System;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class InventoryPresentationTests
    {
        private static readonly string[] DesignSlotIds =
        {
            "head", "neck", "body", "legs", "hands", "feet", "main_hand", "off_hand",
            "ammunition", "ring_1", "ring_2", "tool", "bag"
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
        public void PaperDollPlacesEveryDesignSlotOnceInThreeColumns()
        {
            var cells = EquipmentPaperDollLayout.Arrange(DesignSlotIds, out var rows);

            Assert.That(rows, Is.EqualTo(5));
            Assert.That(cells.Select(cell => cell.SlotId), Is.EquivalentTo(DesignSlotIds));
            Assert.That(
                cells.Select(cell => cell.Row * EquipmentPaperDollLayout.ColumnCount + cell.Column).Distinct().Count(),
                Is.EqualTo(DesignSlotIds.Length));
            Assert.That(cells.All(cell => cell.Column >= 0 && cell.Column < 3), Is.True);
            var head = Cell(cells, "head");
            var body = Cell(cells, "body");
            var mainHand = Cell(cells, "main_hand");
            var offHand = Cell(cells, "off_hand");
            var feet = Cell(cells, "feet");
            Assert.That(head.Column, Is.EqualTo(1));
            Assert.That(head.Row, Is.LessThan(body.Row));
            Assert.That(body.Row, Is.LessThan(feet.Row));
            Assert.That(mainHand.Row, Is.EqualTo(body.Row));
            Assert.That(offHand.Row, Is.EqualTo(body.Row));
            Assert.That(mainHand.Column, Is.LessThan(body.Column));
            Assert.That(offHand.Column, Is.GreaterThan(body.Column));
        }

        [Test]
        public void PaperDollAppendsUnknownSlotsAfterTheSilhouette()
        {
            var cells = EquipmentPaperDollLayout.Arrange(
                DesignSlotIds.Concat(new[] { "cape", "head", " ", null, "quiver", "trinket", "belt" }),
                out var rows);

            Assert.That(rows, Is.EqualTo(7));
            Assert.That(cells.Count, Is.EqualTo(DesignSlotIds.Length + 4));
            Assert.That(Cell(cells, "cape").Row, Is.EqualTo(5));
            Assert.That(Cell(cells, "cape").Column, Is.EqualTo(0));
            Assert.That(Cell(cells, "quiver").Column, Is.EqualTo(1));
            Assert.That(Cell(cells, "trinket").Column, Is.EqualTo(2));
            Assert.That(Cell(cells, "belt").Row, Is.EqualTo(6));

            // The fixture catalog lacks some design slots; its slots still fit the silhouette.
            var fixtureCells = EquipmentPaperDollLayout.Arrange(
                catalog.Gameplay.EquipmentSlots.Select(slot => slot.Id),
                out var fixtureRows);
            Assert.That(fixtureCells.Count, Is.EqualTo(catalog.Gameplay.EquipmentSlots.Length));
            Assert.That(fixtureRows, Is.LessThanOrEqualTo(5));
            Assert.That(EquipmentPaperDollLayout.Arrange(null, out var emptyRows), Is.Empty);
            Assert.That(emptyRows, Is.EqualTo(0));
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
                InventorySlotPresentation.SecureContainerTitle(catalog, "secure_container.base"),
                Is.EqualTo("Fixture Pouch"));
            Assert.That(
                InventorySlotPresentation.SecureContainerTitle(catalog, "secure_container.unknown"),
                Is.EqualTo(InventorySlotPresentation.SecureContainerFallbackTitle));
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
            Assert.That(arrows, Does.Contain("Ammunition  ·  Quantity 30 / 60"));
            Assert.That(arrows, Does.Contain("Weight 30 (1 each)"));
            Assert.That(arrows, Does.Contain("Equips to Ammunition"));
            Assert.That(arrows, Does.EndWith("Equipped in Ammunition"));
            Assert.That(arrows, Does.Not.Contain("Cannot be destroyed"));

            var bow = InventorySlotPresentation.BuildItemTooltip(
                catalog,
                CreateItem("fixture.bow", 1),
                string.Empty);
            Assert.That(bow, Does.Contain("Equips to Main Hand, blocks Off Hand"));
            Assert.That(bow, Does.Contain("Not allowed in the Fixture Pouch"));
            Assert.That(bow, Does.Contain("Weight 25"));
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
            Assert.That(letterTooltip, Does.Contain("Protected on death (Catalog rule)"));
            Assert.That(letterTooltip, Does.Not.Contain("Insured"));
            Assert.That(letterTooltip, Does.Contain("Cannot be destroyed"));
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
                Is.EqualTo("<b>fixture.missing</b>\nQuantity 2\nBank slot 1"));
        }

        private static PaperDollCell Cell(System.Collections.Generic.IEnumerable<PaperDollCell> cells, string slotId)
        {
            return cells.Single(cell => cell.SlotId == slotId);
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
