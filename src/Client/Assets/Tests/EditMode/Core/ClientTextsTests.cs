using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Quests;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // The client shows content text from its text catalog and from nowhere else.
    public sealed class ClientTextsTests
    {
        private ClientConfig config;
        private AsyncOperationHandle<ClientConfig> configHandle;

        [OneTimeSetUp]
        public void LoadConfig()
        {
            config = ClientConfig.Load(out configHandle);
        }

        [OneTimeTearDown]
        public void ReleaseConfig()
        {
            ClientAssets.Release(configHandle);
        }

        [Test]
        public void TheClientConfigurationListsEveryTextTableOnce()
        {
            var tableIds = config.TextTables
                .Select(asset =>
                {
                    Assert.That(asset, Is.Not.Null);
                    Assert.That(ClientTexts.TryParse(asset.text, out var table, out var error), Is.True, error);
                    Assert.That(table.Locale, Is.EqualTo(TextTableFormat.SourceLocale));
                    return table.TableId;
                })
                .ToArray();

            Assert.That(tableIds, Is.EquivalentTo(TextTableIds.All));
        }

        [Test]
        public void TheBundledTablesNameEveryBundledItemAndQuest()
        {
            var texts = FixtureTexts.Bundled(config);
            Assert.That(
                ClientItemCatalog.TryLoad(config.ItemGameplayCatalog.text, texts, out var items, out var error),
                Is.True,
                error);
            Assert.That(
                ClientQuestCatalog.TryParse(config.QuestCatalog.text, texts, out var quests, out error),
                Is.True,
                error);

            Assert.That(texts.Locale, Is.EqualTo(TextTableFormat.SourceLocale));
            Assert.That(texts.Revision, Has.Length.EqualTo(64));
            foreach (var definition in items.Definitions)
            {
                Assert.That(texts.TryGet(TextKeys.ItemName(definition.Id), out var name), Is.True, definition.Id);
                Assert.That(items.GetDisplayName(definition.Id), Is.EqualTo(name));
            }

            foreach (var slot in items.Gameplay.EquipmentSlots)
            {
                Assert.That(items.GetEquipmentSlotName(slot.Id), Does.Not.StartWith("["), slot.Id);
            }

            foreach (var category in items.Gameplay.Categories)
            {
                Assert.That(items.GetCategoryName(category.Id), Does.Not.StartWith("["), category.Id);
            }

            foreach (var vendor in items.Gameplay.Vendors)
            {
                Assert.That(items.GetVendorName(vendor.Id), Does.Not.StartWith("["), vendor.Id);
            }

            Assert.That(items.CurrencyName, Does.Not.StartWith("["));
            foreach (var quest in quests.Quests)
            {
                Assert.That(quest.Title, Does.Not.StartWith("["), quest.Id);
                Assert.That(quest.OfferText, Does.Not.StartWith("["), quest.Id);
                Assert.That(quest.ProgressText, Does.Not.StartWith("["), quest.Id);
                Assert.That(quest.CompletionText, Does.Not.StartWith("["), quest.Id);
                foreach (var objective in quest.Steps.SelectMany(step => step.Objectives))
                {
                    Assert.That(objective.Text, Does.Not.StartWith("["), quest.Id + " " + objective.Id);
                    Assert.That(objective.Line, Does.Not.StartWith("["), quest.Id + " " + objective.Id);
                }
            }
        }

        // The gameplay catalog still holds names, but the client shows the text catalog's.
        [Test]
        public void AnItemNameComesFromTheTextCatalogAndAMissingOneShowsItsKey()
        {
            Assert.That(
                ClientItemCatalog.TryLoad(config.ItemGameplayCatalog.text, FixtureTexts.Bundled(config), out var bundled, out var error),
                Is.True,
                error);
            var definition = bundled.Definitions.First();
            var slot = bundled.Gameplay.EquipmentSlots[0];
            var renamed = FixtureTexts.Of(
                TextTableIds.Items,
                (TextKeys.ItemName(definition.Id), "Renamed By The Table"),
                (TextKeys.EquipmentSlotName(slot.Id), "Renamed Slot"));
            Assert.That(
                ClientItemCatalog.TryCreate(bundled.Gameplay, bundled.Presentation, renamed, out var items, out error),
                Is.True,
                error);

            Assert.That(items.GetDisplayName(definition.Id), Is.EqualTo("Renamed By The Table"));
            Assert.That(items.GetEquipmentSlotName(slot.Id), Is.EqualTo("Renamed Slot"));
            Assert.That(items.CurrencyName, Is.EqualTo("[currency/name]"));
            Assert.That(items.GetDisplayName("not.in.the.catalog"), Is.EqualTo("not.in.the.catalog"));
            Assert.That(items.GetDisplayName(null), Is.Empty);
            if (bundled.Definitions.Count > 1)
            {
                var other = bundled.Definitions.Skip(1).First();
                Assert.That(items.GetDisplayName(other.Id), Is.EqualTo("[" + TextKeys.ItemName(other.Id) + "]"));
            }
        }

        [Test]
        public void QuestTextsComeFromTheTextCatalog()
        {
            var json = config.QuestCatalog.text;
            var runtime = JsonUtility.FromJson<QuestCatalogJsonDocument>(json).ToRuntime();
            var quest = runtime.Quests[0];
            var objective = quest.Steps[0].Objectives[0];
            var renamed = FixtureTexts.Of(
                TextTableIds.Quests,
                (TextKeys.QuestTitle(quest.Id), "Renamed Quest"),
                (TextKeys.QuestObjectiveText(quest.Id, quest.Steps[0].Id, objective.Id), "Renamed objective"));

            Assert.That(ClientQuestCatalog.TryParse(json, renamed, out var catalog, out var error), Is.True, error);
            Assert.That(catalog.TryGet(quest.Id, out var loaded), Is.True);

            Assert.That(loaded.Title, Is.EqualTo("Renamed Quest"));
            Assert.That(loaded.OfferText, Is.EqualTo("[" + TextKeys.QuestOffer(quest.Id) + "]"));
            Assert.That(loaded.Steps[0].Objectives[0].Text, Is.EqualTo("Renamed objective"));
            Assert.That(loaded.Steps[0].Objectives[0].Line, Is.Empty);
            Assert.That(catalog.MatchesServerRevision(runtime.Revision), Is.True);
        }

        [Test]
        public void ACatalogWithoutTextsOrATamperedTableIsRefused()
        {
            var first = config.TextTables[0].text;

            Assert.That(ClientTexts.TryLoad(null, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("no text tables"));
            Assert.That(ClientTexts.TryLoad(new string[] { null }, out _, out error), Is.False);
            Assert.That(error, Does.Contain("missing"));
            Assert.That(ClientTexts.TryParse(string.Empty, out _, out error), Is.False);
            Assert.That(ClientTexts.TryParse("not json", out _, out error), Is.False);
            Assert.That(
                ClientTexts.TryLoad(new[] { first, first }, out _, out error),
                Is.False);
            Assert.That(error, Does.Contain("listed twice"));
            Assert.That(ClientTexts.TryParse(first.Replace("\"text\": \"", "\"text\": \"x"), out _, out error), Is.False);
            Assert.That(error, Does.Contain("revision"));
            Assert.That(ClientItemCatalog.TryLoad(config.ItemGameplayCatalog.text, null, out _, out error), Is.False);
            Assert.That(ClientQuestCatalog.TryParse(config.QuestCatalog.text, null, out _, out error), Is.False);
        }
    }
}
