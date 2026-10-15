using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.Backend.Tests.Unit;

public sealed class RealtimeWorldInteractionOperationsTests
{
    [Fact]
    public async Task CompletionCarriesTheSessionCapturedBeforeCapabilityDispatch()
    {
        var gate = new TestGate();
        using var fixture = new RealtimeLoopFixture(
            enableWorldActors: true,
            capabilityHandlers: [new RealtimeWorldActorInteractionTests.GatedDialogueHandler(gate)]);
        var player = fixture.ConnectPlayer(1);
        var actor = fixture.WorldActors!.ListActors()
            .Single(value => value.Definition.Id == "npc.city_services");
        var opened = fixture.WorldInteractionAuthority!.Process(
                player.Context.PeerId,
                player.Entity,
                RealtimeWorldInteractionIntent.CreateOpen(
                    Guid.NewGuid(),
                    player.Session.SimulationSessionId,
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    actor.NetworkEntityId,
                    actor.RuntimeActorId,
                    actor.InteractionRevision))
            .Opened;
        Assert.NotNull(opened);
        var dialogue = opened.Capabilities.Single(value => value.Id == "services.dialogue");
        var operations = fixture.CreateWorldInteractionOperations();

        var run = operations.RunAsync(
            new IgnoringCompletionHandler(),
            player.Context,
            player.Entity,
            RealtimeWorldInteractionIntent.CreateCapabilityAction(
                Guid.NewGuid(),
                player.Session.SimulationSessionId,
                opened.InteractionSessionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                actor.NetworkEntityId,
                actor.RuntimeActorId,
                actor.InteractionRevision,
                dialogue.Id,
                dialogue.Kind,
                dialogue.Revision));
        Assert.True(gate.HasEntered);
        Assert.False(run.IsCompleted);

        // A same-session reconnect refreshes the entity on the loop during the dispatch.
        player.Entity.RefreshSession(player.Session with
        {
            SimulationSessionToken = "reconnected-session-token"
        });
        gate.Release();
        using var timeout = RealtimeServerHarness.CreateTimeout();
        await run.WaitAsync(timeout.Token);

        Assert.True(fixture.Completions.TryDequeue(out var completion));
        var completed = Assert.IsType<WorldInteractionOperationCompleted>(completion.Operation);
        Assert.Same(player.Session, completed.Session);
        Assert.True(completed.Result.Succeeded);
    }

    private sealed class IgnoringCompletionHandler : IRealtimeCompletionHandler
    {
        public void Complete(RealtimeOperationResult completion)
        {
        }
    }
}
