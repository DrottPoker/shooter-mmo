using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class BodyLookTests
    {
        [Test]
        public void ALookKeepsOneOptionPerSlotInAStableKey()
        {
            var look = BodyLook.Default.With("Face", "Clean").With("Eye", "Brown").With("Face", "Stubble");

            Assert.That(look.Key, Is.EqualTo("Eye=Brown;Face=Stubble"));
            Assert.That(look.OptionFor("Face"), Is.EqualTo("Stubble"));
            Assert.That(look.OptionFor("Hair"), Is.Null);
            Assert.That(look.With("Eye", null).Key, Is.EqualTo("Face=Stubble"));
            Assert.That(BodyLook.Default.Key, Is.Empty);
            Assert.That(look, Is.EqualTo(BodyLook.Default.With("Face", "Stubble").With("Eye", "Brown")));
        }

        [Test]
        public void ALookWearsPaintLayersInItsKey()
        {
            var look = BodyLook.Default.With("Face", "Clean").WithPaint("LeatherJerkin", true).WithPaint("Apron", true);

            Assert.That(look.Key, Is.EqualTo("Face=Clean;Paint=Apron+LeatherJerkin"));
            Assert.That(look.HasPaint("LeatherJerkin"), Is.True);
            Assert.That(BodyLook.Parse(look.Key), Is.EqualTo(look));
            Assert.That(look.WithPaint("Apron", false).Key, Is.EqualTo("Face=Clean;Paint=LeatherJerkin"));
            Assert.That(BodyLook.Parse("Paint=LeatherJerkin+Brö+").Key, Is.EqualTo("Paint=LeatherJerkin"));
            Assert.Throws<System.ArgumentException>(() => BodyLook.Default.With(BodyLook.PaintKey, "LeatherJerkin"));
        }

        [Test]
        public void ALookWearsGarmentsInItsKey()
        {
            var look = BodyLook.Default.With("Face", "Clean").WithGarment("LeatherJerkin", true).WithPaint("Tattoo", true);

            Assert.That(look.Key, Is.EqualTo("Face=Clean;Garment=LeatherJerkin;Paint=Tattoo"));
            Assert.That(look.HasGarment("LeatherJerkin"), Is.True);
            Assert.That(look.HasPaint("LeatherJerkin"), Is.False);
            Assert.That(BodyLook.Parse(look.Key), Is.EqualTo(look));
            Assert.That(look.WithGarment("LeatherJerkin", false).Key, Is.EqualTo("Face=Clean;Paint=Tattoo"));
            Assert.Throws<System.ArgumentException>(() => BodyLook.Default.With(BodyLook.GarmentKey, "LeatherJerkin"));
        }

        [Test]
        public void ALookWearsDyedGarmentsWithTheirColorsInItsKey()
        {
            var look = BodyLook.Default.WithGarment("WoolCloak", new[] { "#2b4364" }).WithGarment("WoolRobe", true);

            Assert.That(look.Key, Is.EqualTo("Garment=WoolCloak@2B4364+WoolRobe"));
            Assert.That(look.DyesFor("WoolCloak"), Is.EqualTo(new[] { (UnityEngine.Color)new UnityEngine.Color32(0x2B, 0x43, 0x64, 255) }));
            Assert.That(look.DyesFor("WoolRobe"), Is.Empty);
            Assert.That(BodyLook.Parse(look.Key), Is.EqualTo(look));
            Assert.That(BodyLook.Parse("Garment=WoolCloak@2B4364.FFFFFF").DyesFor("WoolCloak").Count, Is.EqualTo(2));
            // Taking a garment off forgets its colors, and a broken color skips the garment.
            Assert.That(look.WithGarment("WoolCloak", false).WithGarment("WoolCloak", true).Key, Is.EqualTo("Garment=WoolCloak+WoolRobe"));
            Assert.That(BodyLook.Parse("Garment=WoolCloak@2B436+WoolRobe@ZZZZZZ+Hood@1@2").Key, Is.Empty);
            Assert.That(look.WithoutGarments(), Is.EqualTo(BodyLook.Default));
            Assert.Throws<System.ArgumentException>(() => BodyLook.Default.WithGarment("WoolCloak", new[] { "blue" }));
        }

        [Test]
        public void ALookComesBackFromItsKeyAndSkipsBrokenEntries()
        {
            var look = BodyLook.Parse("Eye=Brown;Face=Clean");

            Assert.That(look.Key, Is.EqualTo("Eye=Brown;Face=Clean"));
            Assert.That(BodyLook.Parse("Face=;=Brown;Eye=Brö;Eye=Teal").Key, Is.EqualTo("Eye=Teal"));
            Assert.That(BodyLook.Parse(null), Is.EqualTo(BodyLook.Default));
        }

        // A catalog with the Face and Eye slots of the male body, without materials or images.
        internal static BodyAppearanceCatalog Catalog(Material face = null, Texture2D clean = null)
        {
            var catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
            catalog.Configure(new[]
            {
                new BodyAppearanceCatalog.Slot("Eye", null, new[]
                {
                    new BodyAppearanceCatalog.Option("Teal", null),
                    new BodyAppearanceCatalog.Option("Brown", null),
                }),
                new BodyAppearanceCatalog.Slot("Face", face, new[]
                {
                    new BodyAppearanceCatalog.Option("Stubble", null),
                    new BodyAppearanceCatalog.Option("Clean", clean),
                }),
            });
            return catalog;
        }
    }
}
