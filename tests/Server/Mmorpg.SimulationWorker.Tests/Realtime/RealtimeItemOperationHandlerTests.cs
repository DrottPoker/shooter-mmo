using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class RealtimeItemOperationHandlerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void CommittedItemWithoutExactlyOneCharacterRevisionDivergesTheSession(
        int characterRevisionCount)
    {
        using var fixture = new RealtimeLoopFixture();
        using var httpClient = new HttpClient(new ScriptedGameServiceHandler())
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var handler = fixture.CreateItemHandler(new ItemAuthorityOperations(
            new SimulationItemInteractionService(
                new GameServiceClient(httpClient),
                new ItemInteractionAccessService(fixture.Config)),
            fixture.Completions,
            NullLogger.Instance));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;
        var intent = RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 0, Guid.NewGuid(), 0);
        var revisions = Enumerable.Range(1, characterRevisionCount)
            .Select(revision => new SimulationItemCharacterRevision(
                player.Session.CharacterId,
                revision,
                0,
                200))
            .ToArray();

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
            "The item transaction returned an invalid character revision set.",
            reason.Message);
        Assert.Null(player.Context.Session);
        Assert.NotNull(player.Context.DisconnectAfterUtc);
        Assert.Empty(fixture.SessionStore.ListActiveSessions());
        Assert.False(fixture.CarryStateStore.TryGet(
            player.Session.CharacterId,
            player.Session.SimulationSessionId,
            out _));
        Assert.Equal(0, fixture.Entities.PlayerCount);
    }
}
