using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class ItemStorageCapabilityHandlerTests
{
    [Theory]
    [InlineData(RealtimeWorldActorCapabilityKind.Bank, WorldActorCapabilityKindIds.Bank, "Character Bank")]
    [InlineData(
        RealtimeWorldActorCapabilityKind.RecoveryStorage,
        WorldActorCapabilityKindIds.RecoveryStorage,
        "Recovery Storage")]
    public async Task OpeningAStorageCapabilityConfirmsItWithoutALifecycleAction(
        RealtimeWorldActorCapabilityKind kind,
        string capabilityKind,
        string expectedName)
    {
        var handler = new ItemStorageWorldActorCapabilityHandler(kind);

        var opened = await handler.ExecuteAsync(
            CreateContext(capabilityKind, RealtimeNpcLifecycleActionKind.None),
            CancellationToken.None);
        var withAction = await handler.ExecuteAsync(
            CreateContext(capabilityKind, RealtimeNpcLifecycleActionKind.ApplyInsurance),
            CancellationToken.None);

        Assert.True(opened.Succeeded);
        Assert.Contains(expectedName, opened.Message, StringComparison.Ordinal);
        Assert.False(withAction.Succeeded);
        Assert.Equal("npc_lifecycle_action_invalid", withAction.Code);
    }

    [Fact]
    public void OnlyStorageKindsCanBeHandled()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ItemStorageWorldActorCapabilityHandler(RealtimeWorldActorCapabilityKind.Insurance));
    }

    private static WorldActorCapabilityOperationContext CreateContext(
        string capabilityKind,
        RealtimeNpcLifecycleActionKind lifecycleAction)
    {
        var session = RealtimeTestSessions.Create();
        var actor = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry())
            .ActivateAssignment("development-world-1", "local-shard-1", "storage-runtime")
            .First(candidate => candidate.Definition.Capabilities.Any(capability =>
                capability.Kind == capabilityKind));
        var capability = actor.Definition.Capabilities.Single(value => value.Kind == capabilityKind);
        var intent = RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            session.SimulationSessionId,
            Guid.NewGuid(),
            RealtimeWorldInteractionTargetKind.WorldActor,
            actor.NetworkEntityId,
            actor.RuntimeActorId,
            actor.InteractionRevision,
            capability.Id,
            WorldActorCapabilityRegistry.ToProtocolKind(capability.Kind),
            1,
            lifecycleAction);
        return new WorldActorCapabilityOperationContext(session, actor, capability, intent);
    }
}
