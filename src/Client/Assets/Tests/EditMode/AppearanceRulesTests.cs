using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A body shows the paint layers of its look by the rules of their categories, as
    // docs/EQUIPMENT_VISUALS_DESIGN.md describes under Layers Of A Character.
    public sealed class AppearanceRulesTests
    {
        [Test]
        public void EveryCategoryHasItsRule()
        {
            // The face and the eyes are chosen once, in character creation.
            Assert.That(AppearanceRules.For(AppearanceCategory.Face), Is.EqualTo(new AppearanceRules.Rule(true, false)));
            Assert.That(AppearanceRules.For(AppearanceCategory.Eye), Is.EqualTo(new AppearanceRules.Rule(true, false)));
            // Underwear is always worn and changes in the game too.
            Assert.That(AppearanceRules.For(AppearanceCategory.Underwear), Is.EqualTo(new AppearanceRules.Rule(true, true)));
            // At most one scar, chosen once, and at most one tattoo, changed in the game too.
            Assert.That(AppearanceRules.For(AppearanceCategory.Scar), Is.EqualTo(new AppearanceRules.Rule(false, false)));
            Assert.That(AppearanceRules.For(AppearanceCategory.Tattoo), Is.EqualTo(new AppearanceRules.Rule(false, true)));
            Assert.That(AppearanceRules.OfSlot("Face"), Is.EqualTo(AppearanceCategory.Face));
            Assert.That(AppearanceRules.OfSlot("Eye"), Is.EqualTo(AppearanceCategory.Eye));
            Assert.That(AppearanceRules.OfSlot("Tattoo"), Is.Null);
            Assert.That(AppearanceRules.OfSlot("Hair"), Is.Null);
        }

        [Test]
        public void ABodyShowsOneOfEachCategoryAndAlwaysItsUnderwear()
        {
            var catalog = Catalog();
            try
            {
                // Without a choice, the first underwear of each slot.
                Assert.That(Shown(catalog, BodyLook.Default, null), Is.EqualTo(new[] { "Bra", "Briefs" }));

                // Of two of a category the first in the catalog, whichever the look names first.
                var look = BodyLook.Default.WithPaint("Burn", true).WithPaint("Slash", true).WithPaint("Rune", true)
                    .WithPaint("LaceBra", true);
                Assert.That(Shown(catalog, look, null), Is.EqualTo(new[] { "Slash", "Rune", "LaceBra", "Briefs" }));

                Assert.That(catalog.OthersLike(Paint(catalog, "Slash")).Select(paint => paint.Name), Is.EqualTo(new[] { "Burn" }));
                Assert.That(catalog.OthersLike(Paint(catalog, "Bra")).Select(paint => paint.Name), Is.EqualTo(new[] { "LaceBra" }));
                Assert.That(catalog.OthersLike(Paint(catalog, "Briefs")), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void AGarmentThatHidesUnderwearHidesOnlyTheUnderwearOfItsSlot()
        {
            var catalog = Catalog();
            try
            {
                var top = Garment("BareShoulders", "body", true);
                Assert.That(Shown(catalog, BodyLook.Default, new[] { top }), Is.EqualTo(new[] { "Briefs" }));
                // Trousers that keep the underwear, and a top that does not hide it, change nothing.
                Assert.That(Shown(catalog, BodyLook.Default, new[] { Garment("Trousers", "legs", false), Garment("Shirt", "body", false) }),
                    Is.EqualTo(new[] { "Bra", "Briefs" }));
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        private static BodyAppearanceCatalog Catalog()
        {
            var catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
            catalog.Configure(
                new BodyAppearanceCatalog.Slot[0],
                new[]
                {
                    Paint("Slash", AppearanceCategory.Scar, string.Empty, 100),
                    Paint("Burn", AppearanceCategory.Scar, string.Empty, 110),
                    Paint("Rune", AppearanceCategory.Tattoo, string.Empty, 200),
                    Paint("Bra", AppearanceCategory.Underwear, "body", 900),
                    Paint("LaceBra", AppearanceCategory.Underwear, "body", 901),
                    Paint("Briefs", AppearanceCategory.Underwear, "legs", 910),
                });
            return catalog;
        }

        private static BodyAppearanceCatalog.Paint Paint(string name, AppearanceCategory category, string slot, int layer)
        {
            return new BodyAppearanceCatalog.Paint(name, category, slot, null, null, layer, 0.5f, 0f);
        }

        private static BodyAppearanceCatalog.Paint Paint(BodyAppearanceCatalog catalog, string name)
        {
            return catalog.Paints.Single(paint => paint.Name == name);
        }

        private static BodyAppearanceCatalog.Garment Garment(string name, string slot, bool hidesUnderwear)
        {
            return new BodyAppearanceCatalog.Garment(name, name, null, slot, 150, new string[0], hidesUnderwear, string.Empty, string.Empty,
                null, new int[0], new int[0], new int[0], new float[0]);
        }

        private static string[] Shown(BodyAppearanceCatalog catalog, BodyLook look, IReadOnlyList<BodyAppearanceCatalog.Garment> drawn)
        {
            var shown = new List<BodyAppearanceCatalog.Paint>();
            catalog.ShownPaints(look, drawn, shown);
            return shown.Select(paint => paint.Name).ToArray();
        }
    }
}
