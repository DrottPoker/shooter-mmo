using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.Backend.Tests.Unit;

public sealed class RealtimeAuthorityQueueTests
{
    [Fact]
    public void QueuedOperationsContinueWhenAWorldInteractionCannotStart()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var handler = fixture.CreateWorldInteractionHandler();
        var player = fixture.ConnectPlayer(1);
        var actor = fixture.WorldActors!.ListActors()
            .Single(value => value.Definition.Id == "npc.city_services");

        // Queue a world interaction behind a running operation, then another operation.
        player.Context.AuthorityOperationInFlight = true;
        handler.HandleIntent(RealtimeLoopFixture.Message(
            player,
            RealtimeMessageType.WorldInteractionIntent,
            RealtimeProtocol.EncodeWorldInteractionIntent(
                RealtimeWorldInteractionIntent.CreateOpen(
                    Guid.NewGuid(),
                    player.Session.SimulationSessionId,
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    actor.NetworkEntityId,
                    actor.RuntimeActorId,
                    actor.InteractionRevision))));
        var next = new RecordingAuthorityOperation();
        player.Context.AuthorityOperationQueue.Enqueue(next);
        Assert.Equal(2, player.Context.AuthorityOperationQueue.Count);

        // The peer is no longer connected when the world interaction is dequeued.
        fixture.Directory.Peers.Remove(player.Context.PeerId);
        player.Context.AuthorityOperationInFlight = false;
        fixture.Scheduler.StartNext(player.Context);

        Assert.True(next.Started);
        Assert.Empty(player.Context.AuthorityOperationQueue);
        Assert.Empty(player.Peer.Sent);
    }

    private sealed class RecordingAuthorityOperation : IRealtimeAuthorityOperation
    {
        public bool Started { get; private set; }

        public void Start(
            RealtimePeerContext context,
            PlayerSimulationEntity entity,
            PlayerMovementState state)
        {
            Started = true;
        }
    }
}
