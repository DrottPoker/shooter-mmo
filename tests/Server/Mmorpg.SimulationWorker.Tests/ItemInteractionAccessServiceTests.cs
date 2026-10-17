using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class ItemInteractionAccessServiceTests
{
    private const int PeerId = 7;

    [Fact]
    public void BankAndRecoveryFollowTheOpenInteractionWithAServiceActor()
    {
        var (leases, actors, access) = CreateAccess(DevelopmentItemInteractionOptions.Disabled);
        var session = RealtimeTestSessions.Create();
        var services = FindActor(actors, WorldActorCapabilityKindIds.Bank);

        Assert.Equal(new ItemInteractionAccess(false, false), access.Evaluate(PeerId, session));

        Assert.True(leases.TryAcquireWorldActor(PeerId, services, out var lease, out _, out _));
        Assert.Equal(new ItemInteractionAccess(true, true), access.Evaluate(PeerId, session));
        Assert.Equal(new ItemInteractionAccess(false, false), access.Evaluate(PeerId + 1, session));

        Assert.True(leases.ReleaseWorldActor(PeerId, lease!.InteractionSessionId));
        Assert.Equal(new ItemInteractionAccess(false, false), access.Evaluate(PeerId, session));
    }

    [Fact]
    public void ActorWithoutStorageCapabilitiesGrantsNoAccess()
    {
        var (leases, actors, access) = CreateAccess(DevelopmentItemInteractionOptions.Disabled);
        var session = RealtimeTestSessions.Create();
        var actor = actors.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "access-runtime")
            .First(candidate => candidate.Definition.Capabilities.All(capability =>
                capability.Kind is not (WorldActorCapabilityKindIds.Bank
                    or WorldActorCapabilityKindIds.RecoveryStorage)));

        Assert.True(leases.TryAcquireWorldActor(PeerId, actor, out _, out _, out _));
        Assert.Equal(new ItemInteractionAccess(false, false), access.Evaluate(PeerId, session));
    }

    [Fact]
    public void DevelopmentGlobalAccessGrantsBankAndRecoveryEverywhere()
    {
        var (_, _, access) = CreateAccess(new DevelopmentItemInteractionOptions(true));

        Assert.Equal(
            new ItemInteractionAccess(true, true),
            access.Evaluate(PeerId, RealtimeTestSessions.Create()));
    }

    private static (WorldInteractionLeaseRegistry Leases, WorldActorStore Actors, ItemInteractionAccessService Access)
        CreateAccess(DevelopmentItemInteractionOptions developmentOptions)
    {
        var leases = new WorldInteractionLeaseRegistry();
        var actors = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry());
        return (
            leases,
            actors,
            new ItemInteractionAccessService(
                developmentOptions,
                leases,
                actors,
                new DefaultWorldActorCapabilityAvailabilityPolicy()));
    }

    private static WorldActorRuntimeState FindActor(WorldActorStore actors, string capabilityKind)
    {
        return actors.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "access-runtime")
            .First(actor => actor.Definition.Capabilities.Any(capability =>
                string.Equals(capability.Kind, capabilityKind, StringComparison.Ordinal)));
    }
}
