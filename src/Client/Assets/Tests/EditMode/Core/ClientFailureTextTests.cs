using System;
using Mmorpg.Client.Api;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Quests;
using Mmorpg.WorldData.Text;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A failure that the client finds itself has a code and an English diagnostic. The player
    // reads the text of the code, or the text for every code without one of its own, and the
    // diagnostic goes to the log only.
    public sealed class ClientFailureTextTests
    {
        private static readonly Guid CharacterId = Guid.Parse("c0000000-0000-4000-8000-000000000003");
        private GameObject controllerObject;

        [SetUp]
        public void SetUp()
        {
            controllerObject = new GameObject("Controllers");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(controllerObject);
        }

        [Test]
        public void AnItemCatalogThatCannotLoadShowsTheTextOfItsCode()
        {
            var texts = TextCatalog.Create(new[]
            {
                FixtureTexts.MessageTable(("item_catalog_invalid", "The items could not be loaded.")),
                FixtureTexts.UiTable()
            });
            var controller = controllerObject.AddComponent<InventoryClientController>();
            var realtime = controllerObject.AddComponent<RealtimeSimulationClient>();

            controller.Initialize(
                new ApiClient(5),
                realtime.Items,
                realtime.Session,
                null,
                texts);

            Assert.That(controller.State.Status, Is.EqualTo(InventoryClientStatus.Error));
            Assert.That(controller.State.Error.Code, Is.EqualTo("item_catalog_invalid"));
            Assert.That(controller.State.Error.ToDisplayMessage(texts), Is.EqualTo("The items could not be loaded."));
        }

        [Test]
        public void ASnapshotThatCannotBeReadShowsTheTextOfItsCode()
        {
            var texts = TextCatalog.Create(new[] { FixtureTexts.MessageTable(), FixtureTexts.UiTable() });
            Assert.That(
                InventorySnapshotMapper.TryMap(
                    new CharacterInventorySnapshotResponse { characterId = "not-a-character" },
                    FixtureItemCatalog.Load(),
                    out _,
                    out var mapError),
                Is.False);
            Assert.That(mapError, Is.Not.Empty);

            var error = InventoryClientError.FromApi(InventoryClientController.InvalidResponse(mapError), texts);

            Assert.That(error.Code, Is.EqualTo("invalid_inventory_response"));
            Assert.That(error.ToDisplayMessage(texts), Is.EqualTo("That did not work. [invalid_inventory_response]"));
        }

        // The correlation id of a failed request names it in the server logs, in a client text.
        [Test]
        public void AFailedRequestShowsItsCorrelationInAClientText()
        {
            var texts = TextCatalog.Create(new[]
            {
                FixtureTexts.MessageTable(("item_state_conflict", "The item changed.")),
                FixtureTexts.UiTable()
            });
            var refused = new ApiError(ApiErrorKind.Http, 409, "item_state_conflict", "Item state changed.", "c-7");

            var error = InventoryClientError.FromApi(refused, texts);

            Assert.That(error.ToDisplayMessage(texts), Is.EqualTo("The item changed. Correlation: c-7."));
            Assert.That(
                new InventoryClientError("inventory_error", string.Empty).ToDisplayMessage(texts),
                Is.EqualTo("That did not work. [inventory_error]"),
                "An error without a text shows the text of its code.");
        }

        [Test]
        public void AQuestCatalogThatCannotLoadShowsTheTextOfItsCode()
        {
            var texts = TextCatalog.Create(new[]
            {
                FixtureTexts.MessageTable(("quest_catalog_invalid", "The quests could not be loaded.")),
                FixtureTexts.UiTable()
            });
            var controller = controllerObject.AddComponent<QuestClientController>();
            var realtime = controllerObject.AddComponent<RealtimeSimulationClient>();

            controller.Initialize(
                new ApiClient(5),
                realtime.Quests,
                realtime.Session,
                null,
                texts);

            Assert.That(controller.State.Status, Is.EqualTo(QuestClientStatus.Failed));
            Assert.That(
                (controller.State.ErrorCode, controller.State.ErrorMessage),
                Is.EqualTo(("quest_catalog_invalid", "The quests could not be loaded.")));
        }

        [Test]
        public void AnOutdatedQuestCatalogShowsTheTextOfItsCode()
        {
            var state = new QuestClientState();
            state.SetCatalog(FixtureQuestCatalog.Load(
                FixtureTexts.MessageTable(("quest_catalog_update_required", "Update the client to see your quests."))));
            state.PrepareCharacter(CharacterId, 1);

            var applied = state.TryApplyLog(
                new CharacterQuestLogResponse
                {
                    characterId = CharacterId.ToString(),
                    questCatalogRevision = new string('0', 64),
                    questLogRevision = 3
                },
                out var diagnostic);

            Assert.That(applied, Is.False);
            Assert.That(diagnostic, Is.Not.Empty);
            Assert.That(state.Status, Is.EqualTo(QuestClientStatus.UpdateRequired));
            Assert.That(
                (state.ErrorCode, state.ErrorMessage),
                Is.EqualTo(("quest_catalog_update_required", "Update the client to see your quests.")));
        }
    }
}
