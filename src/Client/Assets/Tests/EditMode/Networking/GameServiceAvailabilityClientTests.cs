using System;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Quests;
using Mmorpg.Client.Ui;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The worker tells a joined client whether GameService answers. The session follows it and
    // announces each change once, the World shows one line while saving is paused, and a refusal
    // because GameService did not answer leaves every status line once it answers again.
    public sealed class GameServiceAvailabilityClientTests
    {
        private const string Unavailable = RealtimeSession.GameServiceUnavailableCode;
        private static readonly Guid CharacterId = Guid.Parse("c0000000-0000-4000-8000-000000000003");

        private RealtimeHubHarness harness;
        private GameObject owner;
        private UiTheme theme;
        private ApiClient api;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
            owner = new GameObject(nameof(GameServiceAvailabilityClientTests));
            theme = ScriptableObject.CreateInstance<UiTheme>();
            theme.TitleFont = TMP_Settings.defaultFontAsset;
            theme.BodyFont = TMP_Settings.defaultFontAsset;
            api = new ApiClient(5);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(owner);
            UnityEngine.Object.DestroyImmediate(theme);
            api.Dispose();
            harness.Dispose();
        }

        [Test]
        public void TheSessionFollowsTheWorkerAndAnnouncesEachChangeOnce()
        {
            harness.JoinSession();
            Assert.That(harness.Hub.Session.IsGameServiceAvailable, Is.True);

            harness.Deliver(Availability(false));
            harness.Deliver(Availability(false));
            Assert.That(harness.Hub.Session.IsGameServiceAvailable, Is.False);
            harness.Deliver(Availability(true));

            Assert.That(harness.Hub.Session.IsGameServiceAvailable, Is.True);
            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[]
                {
                    "frame",
                    "GameServiceAvailabilityChanged unavailable",
                    "frame",
                    "frame",
                    "GameServiceAvailabilityChanged available"
                }));
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        // A session that ends while GameService does not answer leaves nothing paused, without an
        // announcement, and a reconnect that the worker tells at once is paused again.
        [Test]
        public void AnEndedSessionLeavesNothingPausedAndAReconnectLearnsItAgain()
        {
            harness.JoinSession();
            harness.Deliver(Availability(false));
            harness.Clear();

            harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("game_service_outage", "The shard shut down."));

            Assert.That(harness.Hub.Session.IsGameServiceAvailable, Is.True);
            Assert.That(harness.Timeline, Has.None.StartsWith("GameServiceAvailabilityChanged"));
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION game_service_outage" }));

            harness.BeginJoinAndConnect();
            harness.Clear();
            harness.Queue(RealtimeProtocol.EncodeJoinAccepted(RealtimeHubHarness.JoinAccepted(true)));
            harness.Queue(Availability(false));
            harness.Frame();

            Assert.That(harness.StepJoin(), Is.False);
            Assert.That(harness.Hub.Session.IsJoined, Is.True);
            Assert.That(harness.Hub.Session.IsGameServiceAvailable, Is.False);
            Assert.That(
                harness.Timeline,
                Is.EqualTo(new[] { "frame", "SessionJoined", "GameServiceAvailabilityChanged unavailable" }));
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        [Test]
        public void TheNoticeShowsItsLineWhileSavingIsPausedAndGoesWhenItResumesOrTheSessionEnds()
        {
            var notice = owner.AddComponent<SavingPausedNotice>();
            notice.Initialize(theme, harness.Hub.Session, FixtureTexts.Ui());
            notice.Refresh();
            Assert.That(notice.IsVisible, Is.False, "Outside a session nothing is paused.");

            harness.JoinSession();
            notice.Refresh();
            Assert.That(notice.IsVisible, Is.False);

            harness.Deliver(Availability(false));
            notice.Refresh();
            Assert.That(notice.IsVisible, Is.True);
            Assert.That(
                notice.LineText,
                Is.EqualTo("Saving is paused. Items, trade, and quests come back by themselves."));
            Assert.That(
                owner.GetComponentsInChildren<TextMeshProUGUI>(true),
                Has.Length.EqualTo(1),
                "The notice is one line of TextMesh Pro.");
            Assert.That(
                owner.GetComponentInChildren<Canvas>(true).sortingOrder,
                Is.EqualTo(SavingPausedNotice.CanvasSortingOrder));

            harness.Deliver(Availability(true));
            notice.Refresh();
            Assert.That(notice.IsVisible, Is.False);

            harness.Deliver(Availability(false));
            notice.Refresh();
            Assert.That(notice.IsVisible, Is.True);
            harness.Deliver(RealtimeProtocol.EncodeServerDisconnect("game_service_outage", "The shard shut down."));
            notice.Refresh();
            Assert.That(notice.IsVisible, Is.False);
            Assert.That(harness.TakeFailures(), Is.EqualTo(new[] { "SIMULATION game_service_outage" }));
        }

        // The inventory, a corpse view, a world interaction such as a vendor, gathering, and the
        // quest log each forget a refusal because GameService did not answer once the worker says
        // it answers again, and not before.
        [Test]
        public void RefusalsBecauseGameServiceDidNotAnswerGoWhenItAnswersAgain()
        {
            harness.JoinSession();
            var controllers = Compose();
            ShowRefusals(controllers, Unavailable);

            harness.Deliver(Availability(false));
            Assert.That(Shown(controllers), Is.EqualTo(new[] { Unavailable, Unavailable, Unavailable, Unavailable, "quest refusal" }));

            harness.Deliver(Availability(true));
            Assert.That(Shown(controllers), Is.EqualTo(new[] { "none", "none", "none", "none", "none" }));
            Assert.That(controllers.Inventory.State.Status, Is.EqualTo(InventoryClientStatus.Ready));
            Assert.That(controllers.Interactions.Gathering.Status, Is.EqualTo(GatheringClientStatus.Stopped));
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        [Test]
        public void OtherRefusalsStayWhenGameServiceAnswersAgain()
        {
            harness.JoinSession();
            var controllers = Compose();
            ShowRefusals(controllers, "item_state_conflict");

            harness.Deliver(Availability(false));
            harness.Deliver(Availability(true));

            Assert.That(
                Shown(controllers),
                Is.EqualTo(new[]
                {
                    "item_state_conflict",
                    "item_state_conflict",
                    "item_state_conflict",
                    "item_state_conflict",
                    "quest refusal"
                }));

            // An error that keeps the inventory unusable stays whatever its code.
            controllers.Inventory.State.SetError(new InventoryClientError(Unavailable, "Paused."), false);
            harness.Deliver(Availability(false));
            harness.Deliver(Availability(true));
            Assert.That(controllers.Inventory.State.Error.Code, Is.EqualTo(Unavailable));
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        private static byte[] Availability(bool available)
        {
            return RealtimeProtocol.EncodeGameServiceAvailabilityChanged(available);
        }

        private Controllers Compose()
        {
            var texts = FixtureTexts.Messages();
            var session = harness.Hub.Session;
            var clientSession = new ClientSession(RealtimeHubHarness.Settings);
            var recovery = new ClientSessionRecovery(clientSession, session, new ClientNotices(() => 0f));
            var inventory = owner.AddComponent<InventoryClientController>();
            inventory.Initialize(api, harness.Hub.Items, session, clientSession, recovery, null, texts);
            var corpses = owner.AddComponent<CorpseClientController>();
            corpses.Initialize(harness.Hub.Corpses, session, inventory, texts);
            var actors = owner.AddComponent<WorldActorClientController>();
            actors.Initialize(harness.Hub.WorldActors, session, texts);
            var interactions = owner.AddComponent<WorldInteractionClientController>();
            interactions.Initialize(harness.Hub.WorldInteractions, session, actors, corpses, inventory, texts);
            var quests = owner.AddComponent<QuestClientController>();
            quests.Initialize(api, harness.Hub.Quests, session, clientSession, recovery, null, texts, inventory);
            quests.State.SetCatalog(FixtureQuestCatalog.Load());
            quests.State.PrepareCharacter(CharacterId, 1);
            Assert.That(quests.State.TryApplyLog(QuestLog(), out var error), Is.True, error);
            return new Controllers(inventory, corpses, interactions, quests);
        }

        // Each status line shows a refusal with the code, as the worker's answers leave it.
        private void ShowRefusals(Controllers controllers, string code)
        {
            var error = new RealtimeError(code, "Refused.");
            controllers.Inventory.State.SetServerRejection(new InventoryClientError(code, "Refused."));

            var corpseId = Guid.NewGuid();
            var corpseOperation = Guid.NewGuid();
            controllers.Corpses.State.BeginOperation(corpseOperation, RealtimeCorpseInteractionKind.Open, corpseId);
            Assert.That(
                controllers.Corpses.State.TryComplete(
                    new RealtimeCorpseInteractionResult(
                        corpseOperation,
                        RealtimeCorpseInteractionKind.Open,
                        corpseId,
                        false,
                        false,
                        false,
                        new RealtimeCarryState(3, 0, 100),
                        error),
                    out var failure),
                Is.True,
                failure);

            var interactionOperation = Guid.NewGuid();
            controllers.Interactions.State.Begin(interactionOperation);
            Assert.That(
                controllers.Interactions.State.TryComplete(
                    new RealtimeWorldInteractionResult(
                        interactionOperation,
                        Guid.Empty,
                        RealtimeWorldInteractionOperationKind.CapabilityAction,
                        false,
                        0,
                        0,
                        string.Empty,
                        Array.Empty<RealtimeMessageArgument>(),
                        error),
                    out failure),
                Is.True,
                failure);

            var gatheringSession = Guid.NewGuid();
            var node = Guid.NewGuid();
            var gathering = controllers.Interactions.Gathering;
            Assert.That(
                gathering.TryApply(RealtimeGatheringState.CreateStarted(gatheringSession, 21, node, 3000), gatheringSession, 0f, out failure),
                Is.True,
                failure);
            Assert.That(
                gathering.TryApply(
                    RealtimeGatheringState.CreateStopped(gatheringSession, 21, node, code, "Refused.", Array.Empty<RealtimeMessageArgument>()),
                    gatheringSession,
                    1f,
                    out failure),
                Is.True,
                failure);

            var questOperation = Guid.NewGuid();
            Assert.That(
                controllers.Quests.TryRunAdminTool(RealtimeQuestIntent.CreateAdminResetAll(questOperation), out failure),
                Is.True,
                failure);
            harness.Deliver(RealtimeProtocol.EncodeQuestIntentResult(new RealtimeQuestIntentResult(
                questOperation,
                RealtimeQuestIntentKind.AdminResetAll,
                false,
                0,
                error)));
        }

        // The code each status line shows, or "none", and whether the quest log shows a refusal.
        private static string[] Shown(Controllers controllers)
        {
            return new[]
            {
                controllers.Inventory.State.Error?.Code ?? "none",
                controllers.Corpses.State.Error?.Code ?? "none",
                NoneIfEmpty(controllers.Interactions.State.LastErrorCode),
                NoneIfEmpty(controllers.Interactions.Gathering.StopCode),
                string.IsNullOrEmpty(controllers.Quests.LastIntentError) ? "none" : "quest refusal"
            };
        }

        private static string NoneIfEmpty(string code)
        {
            return string.IsNullOrEmpty(code) ? "none" : code;
        }

        private static CharacterQuestLogResponse QuestLog()
        {
            return new CharacterQuestLogResponse
            {
                characterId = CharacterId.ToString(),
                questCatalogRevision = FixtureQuestCatalog.Load().Revision,
                questLogRevision = 3,
                activeQuests = Array.Empty<ActiveQuestResponse>(),
                completedQuests = Array.Empty<CompletedQuestResponse>()
            };
        }

        private sealed class Controllers
        {
            public Controllers(
                InventoryClientController inventory,
                CorpseClientController corpses,
                WorldInteractionClientController interactions,
                QuestClientController quests)
            {
                Inventory = inventory;
                Corpses = corpses;
                Interactions = interactions;
                Quests = quests;
            }

            public InventoryClientController Inventory { get; }

            public CorpseClientController Corpses { get; }

            public WorldInteractionClientController Interactions { get; }

            public QuestClientController Quests { get; }
        }
    }
}
