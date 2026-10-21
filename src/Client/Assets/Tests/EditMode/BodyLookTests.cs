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
            var look = BodyLook.Default.With("Face", "Scarred").With("Eye", "Brown").With("Face", "Stubble");

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
            var look = BodyLook.Default.With("Face", "Scarred").WithPaint("LeatherJerkin", true).WithPaint("Apron", true);

            Assert.That(look.Key, Is.EqualTo("Face=Scarred;Paint=Apron+LeatherJerkin"));
            Assert.That(look.HasPaint("LeatherJerkin"), Is.True);
            Assert.That(BodyLook.Parse(look.Key), Is.EqualTo(look));
            Assert.That(look.WithPaint("Apron", false).Key, Is.EqualTo("Face=Scarred;Paint=LeatherJerkin"));
            Assert.That(BodyLook.Parse("Paint=LeatherJerkin+Brö+").Key, Is.EqualTo("Paint=LeatherJerkin"));
            Assert.Throws<System.ArgumentException>(() => BodyLook.Default.With(BodyLook.PaintKey, "LeatherJerkin"));
        }

        [Test]
        public void ALookComesBackFromItsKeyAndSkipsBrokenEntries()
        {
            var look = BodyLook.Parse("Eye=Brown;Face=Scarred");

            Assert.That(look.Key, Is.EqualTo("Eye=Brown;Face=Scarred"));
            Assert.That(BodyLook.Parse("Face=;=Brown;Eye=Brö;Eye=Teal").Key, Is.EqualTo("Eye=Teal"));
            Assert.That(BodyLook.Parse(null), Is.EqualTo(BodyLook.Default));
        }

        [Test]
        public void AVariedLookIsTheSameForASeedAndCoversEveryOption()
        {
            var catalog = Catalog();
            try
            {
                Assert.That(BodyLook.Varied("bot-7", catalog), Is.EqualTo(BodyLook.Varied("bot-7", catalog)));
                // Every combination shows up: the slots of one seed pick independently.
                var looks = new HashSet<string>();
                for (var index = 0; index < 64; index++)
                {
                    looks.Add(BodyLook.Varied("bot-" + index, catalog).Key);
                }

                Assert.That(looks, Is.EquivalentTo(new[]
                {
                    "Eye=Brown;Face=Scarred", "Eye=Brown;Face=Stubble", "Eye=Teal;Face=Scarred", "Eye=Teal;Face=Stubble",
                }));
                Assert.That(BodyLook.Varied("bot-7", null), Is.EqualTo(BodyLook.Default));
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void TheStableHashIsFowlerNollVo()
        {
            Assert.That(BodyLook.StableHash(string.Empty), Is.EqualTo(2166136261u));
            Assert.That(BodyLook.StableHash("a"), Is.EqualTo(0xE40C292Cu));
        }

        [Test]
        public void OnlyDevelopmentBotsShowAVariedLook()
        {
            var catalog = Catalog();
            try
            {
                Assert.That(
                    RemotePlayerView.LookFor(RealtimePlayerArchetypeIds.DevelopmentBot, "bot-3", catalog),
                    Is.EqualTo(BodyLook.Varied("bot-3", catalog)));
                Assert.That(
                    RemotePlayerView.LookFor(RealtimePlayerArchetypeIds.Player, "character-3", catalog),
                    Is.EqualTo(BodyLook.Default));
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        // A catalog with the Face and Eye slots of the male body, without materials or images.
        internal static BodyAppearanceCatalog Catalog(Material face = null, Texture2D scarred = null)
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
                    new BodyAppearanceCatalog.Option("Scarred", scarred),
                }),
            });
            return catalog;
        }
    }
}
