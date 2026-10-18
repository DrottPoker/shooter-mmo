using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class RealtimeItemOperationHandlerTests
{
    // The GameService client never accepts such a response; the handler still ends the
    // session instead of trusting it.
    [Fact]
    public void CommittedItemWithoutCharacterStateDivergesTheSession()
    {
        using var fixture = new RealtimeLoopFixture();
        using var httpClient = new HttpClient(new ScriptedGameServiceHandler())
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var handler = fixture.CreateItemHandler(new ItemAuthorityOperations(
            new SimulationItemInteractionService(new GameServiceClient(httpClient)),
            fixture.Completions,
            NullLogger.Instance));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;
        var intent = RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 0, Guid.NewGuid(), 0);
        var revisions = new[] { new SimulationItemCharacterRevision(player.Session.CharacterId, 1, 0, 200) };

        handler.Complete(new ItemOperationCompleted(
            player.Context,
            player.Session,
            SimulationItemInteractionResult.Committed(
                intent,
                new SimulationItemTransactionResponse(
                    intent.OperationId,
                    "destroy",
                    true,
                    null,
                    revisions,
                    [],
                    [],
                    [],
                    null))));

        var sent = Assert.Single(player.Peer.Sent);
        Assert.Equal(DeliveryMethod.ReliableOrdered, sent.Delivery);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(sent.Data, out var reason, out _));
        Assert.Equal("item_state_diverged", reason.Code);
        Assert.Equal(
            "The item transaction returned no committed character state.",
            reason.Message);
        Assert.Null(player.Context.Session);
        Assert.NotNull(player.Context.DisconnectAfterUtc);
        Assert.Empty(fixture.SessionStore.ListActiveSessions());
        Assert.False(fixture.CharacterItemStateStore.TryGet(
            player.Session.CharacterId,
            player.Session.SimulationSessionId,
            out _));
        Assert.Equal(0, fixture.Entities.PlayerCount);
    }
}
