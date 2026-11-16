using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using static Mmorpg.Client.Tests.EditMode.RealtimeHubMessages;

namespace Mmorpg.Client.Tests.EditMode
{
    // The world channel carries the actors and the owner channel the interactions, so an actor's
    // newer state or its despawn can arrive before the opening that the worker sent first. The
    // worker closes such an interaction in order on the owner channel; the client neither fails
    // nor shows an error for it.
    public sealed class WorldInteractionChannelOrderTests
    {
        private RealtimeHubHarness harness;
        private GameObject owner;
        private ApiClient api;
        private WorldInteractionClientController interactions;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
            owner = new GameObject(nameof(WorldInteractionChannelOrderTests));
            api = new ApiClient(5);
            harness.JoinSession();
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
            interactions = owner.AddComponent<WorldInteractionClientController>();
            interactions.Initialize(harness.Hub.WorldInteractions, session, actors, corpses, inventory, texts);
            harness.Deliver(RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawn()));
            harness.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(owner);
            api.Dispose();
            harness.Dispose();
        }

        // The opening names interaction revision 4, but the actor's state with revision 5 came
        // first: the interaction opens, and the worker's closure that follows ends it.
        [Test]
        public void AnOpeningOlderThanTheActorsNewestStateOpensUntilTheWorkerClosesIt()
        {
            harness.Deliver(RealtimeProtocol.EncodeWorldActorState(WorldActorState()));
            interactions.State.Begin(InteractionOpened().OperationId);

            harness.Deliver(RealtimeProtocol.EncodeWorldInteractionOpened(InteractionOpened()));

            Assert.That(harness.Hub.Session.IsJoined, Is.True);
            Assert.That(interactions.State.ActiveInteraction, Is.Not.Null);
            harness.Deliver(RealtimeProtocol.EncodeWorldInteractionClosed(InteractionClosed()));
            Assert.That(interactions.State.ActiveInteraction, Is.Null);
            Assert.That(interactions.State.LastErrorCode, Is.EqualTo("target_despawned"));
            Assert.That(harness.TakeFailures(), Is.Empty);
        }

        // The actor despawned before the opening arrived: nothing opens, the pending opening ends
        // without a message, and the worker's closure that follows changes nothing.
        [Test]
        public void AnOpeningForAnActorThatAlreadyLeftOpensNothingAndFailsNothing()
        {
            interactions.State.Begin(InteractionOpened().OperationId);
            harness.Deliver(RealtimeProtocol.EncodeWorldActorDespawn(WorldActorDespawn()));

            harness.Deliver(RealtimeProtocol.EncodeWorldInteractionOpened(InteractionOpened()));
            harness.Deliver(RealtimeProtocol.EncodeWorldInteractionClosed(InteractionClosed()));

            Assert.That(harness.Hub.Session.IsJoined, Is.True);
            Assert.That(interactions.State.ActiveInteraction, Is.Null);
            Assert.That(interactions.State.PendingOperationId, Is.EqualTo(System.Guid.Empty));
            Assert.That(interactions.State.LastErrorCode, Is.Empty);
            Assert.That(harness.TakeFailures(), Is.Empty);
        }
    }
}
