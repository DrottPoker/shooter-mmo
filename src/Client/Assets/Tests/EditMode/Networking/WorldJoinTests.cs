using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The join request that every way into a World sends, so GameService can refuse a client that
    // does not fit the Shard's worker before it issues a ticket.
    public sealed class WorldJoinTests
    {
        private RealtimeHubHarness harness;

        [SetUp]
        public void SetUp()
        {
            harness = new RealtimeHubHarness();
        }

        [TearDown]
        public void TearDown()
        {
            harness.Dispose();
        }

        [Test]
        public void TheRequestNamesTheVersionsOfThisClientAndTheBakedCollisionRevisionOfTheWorld()
        {
            var request = WorldJoin.CreateRequest("character-1", RealtimeHubMessages.WorldId);

            Assert.That(request.characterId, Is.EqualTo("character-1"));
            Assert.That(request.protocolVersion, Is.EqualTo(RealtimeProtocol.Version));
            Assert.That(request.simulationRevision, Is.EqualTo(GameSimulationCompatibility.Revision));
            Assert.That(request.worldId, Is.EqualTo(RealtimeHubMessages.WorldId));
            Assert.That(request.collisionRevision, Is.EqualTo(RealtimeHubHarness.BakedCollisionRevision));
        }

        // A client without the collision data of a World names no World, so GameService judges
        // only the versions of its code, and the later check says why the World cannot load.
        [TestCase("a-world-this-client-does-not-have")]
        [TestCase("")]
        [TestCase(null)]
        public void AWorldWithoutBakedCollisionDataNamesNoWorldAndNoCollisionRevision(string worldId)
        {
            var request = WorldJoin.CreateRequest("character-1", worldId);

            Assert.That(request.protocolVersion, Is.EqualTo(RealtimeProtocol.Version));
            Assert.That(request.simulationRevision, Is.EqualTo(GameSimulationCompatibility.Revision));
            Assert.That(request.worldId, Is.Null.Or.Empty);
            Assert.That(request.collisionRevision, Is.Null.Or.Empty);
        }

        // The whole path: the request that the join sends carries the versions, and GameService's
        // refusal of them reaches the caller as it came, with its code.
        [Test]
        public void TheJoinSendsTheRequestWithTheVersionsAndPassesOnGameServicesRefusal()
        {
            var session = new ClientSession(RealtimeHubHarness.Settings);
            session.SignIn(new AuthResponse { accountId = "account-1", username = "hub_hero", sessionToken = "token" });
            var tickets = new FakeJoinTickets();
            var join = new WorldJoin(session, tickets, harness.Hub.Session);
            WorldJoinResult result = null;
            var routine = new TestRoutine(join.Run(
                "character-1",
                "Hero",
                RealtimeHubMessages.ShardId,
                RealtimeHubMessages.WorldId,
                null,
                value => result = value));

            Assert.That(routine.Step(), Is.True);
            var sent = tickets.Last.Sent;
            Assert.That(sent.protocolVersion, Is.EqualTo(RealtimeProtocol.Version));
            Assert.That(sent.simulationRevision, Is.EqualTo(GameSimulationCompatibility.Revision));
            Assert.That(sent.collisionRevision, Is.EqualTo(RealtimeHubHarness.BakedCollisionRevision));

            tickets.Last.Refuse(new ApiError(ApiErrorKind.Http, 409, "incompatible_protocol", "Update.", string.Empty));
            Assert.That(routine.Step(), Is.False);

            Assert.That(result.Status, Is.EqualTo(WorldJoinStatus.TicketRefused));
            Assert.That(result.TicketError.Code, Is.EqualTo("incompatible_protocol"));
            Assert.That(harness.Transport.IsStarted, Is.False, "No transport starts for a refused ticket.");
        }
    }
}
