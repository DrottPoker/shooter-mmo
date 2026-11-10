using System.Linq;
using System.Reflection;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor.Ui;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The bundled UI theme is complete: every sprite and font is set, every equipment slot
    // the windows show and every tag of a specialized Bag slot has its silhouette, and the
    // fonts carry the characters the UI draws, so no text reaches the dynamic fallback.
    public sealed class UiThemeTests
    {
        private UiTheme theme;

        [SetUp]
        public void SetUp()
        {
            theme = AssetDatabase.LoadAssetAtPath<UiTheme>(UiThemeBuilder.ThemePath);
            Assert.That(theme, Is.Not.Null, UiThemeBuilder.ThemePath);
        }

        [Test]
        public void EverySpriteAndFontOfTheThemeIsSet()
        {
            var missing = typeof(UiTheme)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(field => field.FieldType == typeof(Sprite) && field.GetValue(theme) == null)
                .Select(field => field.Name)
                .ToArray();

            Assert.That(missing, Is.Empty);
            Assert.That(theme.TitleFont, Is.Not.Null);
            Assert.That(theme.BodyFont, Is.Not.Null);
            Assert.That(theme.FallbackSilhouette, Is.Not.Null);
        }

        // A frame sprite keeps its corners when it stretches, so it is nine-sliced.
        [Test]
        public void FrameSpritesAreNineSliced()
        {
            foreach (var sprite in new[]
            {
                theme.WindowFrame,
                theme.DialogFrame,
                theme.TooltipFrame,
                theme.SlotBackground,
                theme.ItemFrame,
                theme.SlotHighlight,
                theme.SlotGlow,
                theme.ButtonFrame,
                theme.FieldFrame,
                theme.MeterTrack,
                theme.MeterFill
            })
            {
                Assert.That(sprite.border, Is.Not.EqualTo(Vector4.zero), sprite.name);
                Assert.That(sprite.border.x + sprite.border.z, Is.LessThan(sprite.rect.width), sprite.name);
                Assert.That(sprite.border.y + sprite.border.w, Is.LessThan(sprite.rect.height), sprite.name);
            }
        }

        [Test]
        public void EveryShownEquipmentSlotAndBagTagHasASilhouette()
        {
            var config = ClientConfig.Load(out var configHandle);
            ClientItemCatalog catalog;
            try
            {
                Assert.That(ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, FixtureTexts.Bundled(config), out catalog, out var error), Is.True, error);
            }
            finally
            {
                ItemPresentationCatalogLoader.ResetCache();
                ClientAssets.Release(configHandle);
            }

            var shown = EquipmentPaperDollLayout.Arrange(catalog.Gameplay.EquipmentSlots.Select(slot => slot.Id)).All;
            foreach (var slotId in shown)
            {
                Assert.That(
                    theme.EquipmentSilhouettes.Any(entry => entry.Id == slotId && entry.Sprite != null),
                    Is.True,
                    slotId);
            }

            var tags = catalog.Definitions
                .Where(definition => definition.Bag != null)
                .SelectMany(definition => definition.Bag.Slots)
                .SelectMany(slot => slot.AcceptedTags ?? new string[0])
                .Distinct();
            foreach (var tag in tags)
            {
                Assert.That(theme.TagSilhouettes.Any(entry => entry.Id == tag && entry.Sprite != null), Is.True, tag);
            }
        }

        // Static atlases never change at run time, so every UI font holds the printable
        // characters and the punctuation, the ellipsis among them.
        [Test]
        public void TheUiFontsAreStaticAndHoldTheUiCharacters()
        {
            foreach (var path in new[]
            {
                UiThemeBuilder.TitleFontPath,
                UiThemeBuilder.BodyFontPath,
                UiThemeBuilder.BodyBoldFontPath,
                UiThemeBuilder.FallbackFontPath
            })
            {
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                Assert.That(font, Is.Not.Null, path);
                Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static), path);
                foreach (var character in "Aa0 \u00E5\u00E4\u00F6\u00B7\u2026\u2013\u2014\u2018\u2019\u201C\u201D\u2022")
                {
                    Assert.That(font.HasCharacter(character), Is.True, path + " lacks U+" + ((int)character).ToString("X4"));
                }
            }

            var body = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(UiThemeBuilder.BodyFontPath);
            Assert.That(body.fontWeightTable[7].regularTypeface, Is.Not.Null, "Bold text uses the bold face.");
            Assert.That(theme.BodyFont, Is.SameAs(body));
        }
    }
}
