using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

// Resource node charges, depletion, respawn generations, and the central timer queue.
public sealed class ResourceNodeRuntimeTests
{
    [Fact]
    public void ActivationGivesResourceNodesTheirProfileFullChargesAndEventDrivenTier()
    {
        var vein = ActivateVein();

        Assert.NotNull(vein.ResourceProfile);
        Assert.Equal("resource.iron_vein", vein.ResourceProfile!.Id);
        Assert.Equal(vein.ResourceProfile.Charges, vein.RemainingResourceCharges);
        Assert.Equal(0, vein.ReservedResourceCharges);
        Assert.Equal(WorldActorActivityTierIds.EventDriven, vein.ActivityTier);
        Assert.True(vein.IsActive);
    }

    [Fact]
    public void ReservationsNeverExceedTheRemainingCharges()
    {
        var vein = ActivateVein();
        var charges = vein.RemainingResourceCharges;

        for (var index = 0; index < charges; index++)
        {
            Assert.True(vein.TryReserveResourceCharge());
        }

        Assert.False(vein.TryReserveResourceCharge());
        vein.ReleaseResourceCharge(vein.ResourceGeneration);
        Assert.True(vein.TryReserveResourceCharge());
    }

    [Fact]
    public void ConsumingTheLastChargeDepletesTheNodeAndAdvancesItsRevisions()
    {
        var vein = ActivateVein();
        var generation = vein.ResourceGeneration;
        var interactionRevision = vein.InteractionRevision;
        var charges = vein.RemainingResourceCharges;

        for (var index = 0; index < charges - 1; index++)
        {
            Assert.True(vein.TryReserveResourceCharge());
            Assert.False(vein.ConsumeResourceCharge(generation));
        }

        Assert.True(vein.TryReserveResourceCharge());
        Assert.True(vein.ConsumeResourceCharge(generation));

        Assert.False(vein.IsActive);
        Assert.Equal(0, vein.RemainingResourceCharges);
        Assert.Equal(generation + 1, vein.ResourceGeneration);
        Assert.True(vein.InteractionRevision > interactionRevision);
        Assert.False(vein.TryReserveResourceCharge());
    }

    [Fact]
    public void ReplenishRestoresFullChargesOnlyForTheCurrentGeneration()
    {
        var vein = ActivateVein();
        Deplete(vein);

        Assert.False(vein.ReplenishResource(vein.ResourceGeneration - 1));
        Assert.False(vein.IsActive);
        Assert.True(vein.ReplenishResource(vein.ResourceGeneration));
        Assert.True(vein.IsActive);
        Assert.Equal(vein.ResourceProfile!.Charges, vein.RemainingResourceCharges);
        Assert.False(vein.ReplenishResource(vein.ResourceGeneration));
    }

    [Fact]
    public void StaleCompletionsNeverTouchANewerCycle()
    {
        var vein = ActivateVein();
        var oldGeneration = vein.ResourceGeneration;
        Deplete(vein);
        Assert.True(vein.ReplenishResource(vein.ResourceGeneration));
        Assert.True(vein.TryReserveResourceCharge());

        vein.ReleaseResourceCharge(oldGeneration);
        Assert.False(vein.ConsumeResourceCharge(oldGeneration));

        Assert.Equal(1, vein.ReservedResourceCharges);
        Assert.Equal(vein.ResourceProfile!.Charges, vein.RemainingResourceCharges);
    }

    [Fact]
    public void NonResourceActorsHaveNoCharges()
    {
        var store = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry());
        var npc = store.ActivateAssignment("development-world-1", "local-shard-1", "resource-runtime")
            .Single(actor => actor.Definition.Kind == WorldActorKindIds.Npc
                && actor.Definition.Id == "npc.city_services");

        Assert.Null(npc.ResourceProfile);
        Assert.False(npc.TryReserveResourceCharge());
    }

    [Fact]
    public void TimerQueueDrainsDueTimersInDueOrder()
    {
        var queue = new WorldActorTimerQueue();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var later = Guid.NewGuid();
        queue.Schedule(20, second, 1);
        queue.Schedule(10, first, 1);
        queue.Schedule(40, later, 1);
        var due = new List<WorldActorTimer>();

        queue.DrainDue(9, due);
        Assert.Empty(due);
        queue.DrainDue(20, due);

        Assert.Equal([first, second], due.Select(timer => timer.RuntimeActorId));
        Assert.Equal(1, queue.Count);
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Schedule(1, Guid.Empty, 1));
    }

    private static WorldActorRuntimeState ActivateVein()
    {
        var store = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry());
        return store.ActivateAssignment("development-world-1", "local-shard-1", "resource-runtime")
            .Single(actor => actor.Definition.Kind == WorldActorKindIds.ResourceNode);
    }

    private static void Deplete(WorldActorRuntimeState vein)
    {
        var generation = vein.ResourceGeneration;
        while (vein.TryReserveResourceCharge())
        {
            vein.ConsumeResourceCharge(generation);
        }
    }
}
