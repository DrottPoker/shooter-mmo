using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Drives the loop-confined gathering handler tick by tick. Without a gathering service
// every cycle completes with gathering_unavailable, which a test replaces with the
// outcome it needs while keeping the real request, generation, and operation id.
public sealed class RealtimeGatheringTests
{
    private const string CapabilityId = "resource.mine";

    [Fact]
    public void ACycleReservesAChargeAndAYieldConsumesItAndReportsTheCarryRevision()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        var charges = vein.RemainingResourceCharges;

        Begin(fixture, gathering, player, vein);

        var started = Assert.Single(States(player));
        Assert.Equal(RealtimeGatheringStateKind.Started, started.Kind);
        Assert.Equal(3000, started.CycleMilliseconds);
        Assert.Equal(vein.NetworkEntityId, started.TargetEntityId);
        Assert.Equal(vein.RuntimeActorId, started.TargetRuntimeId);

        var cycle = RunCycle(fixture, gathering, out var ticks);
        Assert.Equal(3 * fixture.Config.MovementSimulation.TickRateHz, ticks);
        Assert.Equal(1, vein.ReservedResourceCharges);
        Assert.Equal(CapabilityId, cycle.Request.CapabilityId);
        Assert.Equal(vein.Spawn.Id, cycle.Request.ResourceNodeId);
        Assert.Equal("resource.iron_vein", cycle.Request.Profile.Id);
        Assert.Equal(vein.ResourceGeneration, cycle.Generation);

        gathering.Complete(Yielded(cycle, player, 1));

        Assert.Equal(charges - 1, vein.RemainingResourceCharges);
        Assert.Equal(0, vein.ReservedResourceCharges);
        Assert.False(player.Context.AuthorityOperationInFlight);
        var yielded = States(player).Last();
        Assert.Equal(RealtimeGatheringStateKind.Yielded, yielded.Kind);
        Assert.Equal("material.iron_ore", yielded.YieldDefinitionId);
        Assert.Equal(1, yielded.YieldQuantity);
        Assert.Equal(1, yielded.ItemStateRevision);
        Assert.True(fixture.CharacterItemStateStore.TryGet(
            player.Session.CharacterId,
            player.Session.SimulationSessionId,
            out var carry));
        Assert.Equal(1, carry!.ItemStateRevision);
        Assert.Equal(1, gathering.ActiveCount);

        var next = RunCycle(fixture, gathering, out _);
        Assert.NotEqual(cycle.Request.OperationId, next.Request.OperationId);
    }

    [Fact]
    public void AYieldThatCommitsAfterGatheringStoppedStillReachesTheClient()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        var charges = vein.RemainingResourceCharges;
        var first = Begin(fixture, gathering, player, vein);
        var cycle = RunCycle(fixture, gathering, out _);

        // A new gathering start stops the first one while its yield is still in flight.
        gathering.Begin(player.Peer, player.Context, player.Session, new GatheringStart(Guid.NewGuid(), CapabilityId, vein));
        Assert.Equal(GatheringStopCodes.Interrupted, States(player)[^2].Code);
        gathering.Complete(Yielded(cycle, player, 1));

        var yielded = States(player).Last();
        Assert.Equal(RealtimeGatheringStateKind.Yielded, yielded.Kind);
        Assert.Equal(first.InteractionSessionId, yielded.InteractionSessionId);
        Assert.Equal(1, yielded.ItemStateRevision);
        Assert.Equal(charges - 1, vein.RemainingResourceCharges);
        Assert.Equal(0, vein.ReservedResourceCharges);
    }

    [Fact]
    public void AGatheringPlayerUsesItsToolSlotUntilGatheringStops()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        Assert.Equal(string.Empty, player.Entity.UsedEquipmentSlot);

        Begin(fixture, gathering, player, vein);
        Assert.Equal("tool", player.Entity.UsedEquipmentSlot);

        // Another start replaces the activity, and the player keeps using its tool.
        gathering.Begin(player.Peer, player.Context, player.Session, new GatheringStart(Guid.NewGuid(), CapabilityId, vein));
        Assert.Equal("tool", player.Entity.UsedEquipmentSlot);

        // That start holds no interaction with the node, so the next tick stops it.
        gathering.Process();
        Assert.Equal(GatheringStopCodes.InteractionClosed, States(player).Last().Code);
        Assert.Equal(0, gathering.ActiveCount);
        Assert.Equal(string.Empty, player.Entity.UsedEquipmentSlot);
    }

    [Fact]
    public void ARejectedYieldStopsGatheringAndReleasesTheCharge()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        var charges = vein.RemainingResourceCharges;
        Begin(fixture, gathering, player, vein);

        gathering.Complete(RunCycle(fixture, gathering, out _));

        Assert.Equal(charges, vein.RemainingResourceCharges);
        Assert.Equal(0, vein.ReservedResourceCharges);
        Assert.Equal(0, gathering.ActiveCount);
        Assert.False(player.Context.AuthorityOperationInFlight);
        var stopped = States(player).Last();
        Assert.Equal(RealtimeGatheringStateKind.Stopped, stopped.Kind);
        Assert.Equal("gathering_unavailable", stopped.Code);
        Assert.NotNull(player.Context.Session);
    }

    [Fact]
    public void AnUncertainOutcomeRetriesWithTheSameOperationBeforeStopping()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        Begin(fixture, gathering, player, vein);
        var cycle = RunCycle(fixture, gathering, out _);
        var operationId = cycle.Request.OperationId;

        for (var retry = 0; retry < 3; retry++)
        {
            gathering.Complete(Failed(cycle, "auth_service_timeout"));
            Assert.Equal(0, vein.ReservedResourceCharges);
            cycle = RunCycle(fixture, gathering, out _);
            Assert.Equal(operationId, cycle.Request.OperationId);
        }

        gathering.Complete(Failed(cycle, "auth_service_timeout"));

        Assert.Equal(0, gathering.ActiveCount);
        Assert.Equal("auth_service_timeout", States(player).Last().Code);
    }

    [Fact]
    public void AnEndedItemSessionDisconnectsThePlayer()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        Begin(fixture, gathering, player, vein);

        gathering.Complete(Failed(RunCycle(fixture, gathering, out _), "wrong_simulation_worker"));

        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(player.Peer.Sent[^1].Data, out var reason, out _));
        Assert.Equal("wrong_simulation_worker", reason.Code);
        Assert.Null(player.Context.Session);
        Assert.Equal(0, vein.ReservedResourceCharges);
    }

    [Fact]
    public void TheLastChargeDepletesTheNodeForEveryGathererAndTheTimerRespawnsIt()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var first = fixture.ConnectPlayer(1, "First Miner");
        var second = fixture.ConnectPlayer(2, "Second Miner");
        var vein = Vein(fixture);
        var generation = vein.ResourceGeneration;
        while (vein.RemainingResourceCharges > 1)
        {
            Assert.True(vein.TryReserveResourceCharge());
            Assert.False(vein.ConsumeResourceCharge(generation));
        }

        Begin(fixture, gathering, first, vein);
        Begin(fixture, gathering, second, vein);

        // Only one gatherer gets the last charge; the other waits for it.
        var cycle = RunCycle(fixture, gathering, out _);
        Assert.Equal(0, fixture.Completions.Count);
        var winner = cycle.PeerContext.PeerId == first.Context.PeerId ? first : second;
        gathering.Complete(Yielded(cycle, winner, 1));

        Assert.False(vein.IsActive);
        Assert.Equal(0, vein.RemainingResourceCharges);
        Assert.Equal(generation + 1, vein.ResourceGeneration);
        Assert.Equal(0, gathering.ActiveCount);
        Assert.Equal(1, fixture.Timers.Count);
        Assert.Equal(RealtimeGatheringStateKind.Yielded, States(winner)[^2].Kind);
        foreach (var player in new[] { first, second })
        {
            var stopped = States(player).Last();
            Assert.Equal(RealtimeGatheringStateKind.Stopped, stopped.Kind);
            Assert.Equal(GatheringStopCodes.Depleted, stopped.Code);
        }

        var driver = new WorldActorRuntimeDriver(
            fixture.Config,
            fixture.WorldActors,
            null,
            null,
            null,
            fixture.Presence,
            fixture.Metrics,
            fixture.Timers,
            NullLogger.Instance);
        var dueTick = fixture.Clock.ServerTick + (30 * fixture.Config.MovementSimulation.TickRateHz);
        driver.EvaluateTick([], (uint)dueTick - 1);
        Assert.False(vein.IsActive);
        driver.EvaluateTick([], (uint)dueTick);

        Assert.True(vein.IsActive);
        Assert.Equal(vein.ResourceProfile!.Charges, vein.RemainingResourceCharges);
        Assert.Equal(0, fixture.Timers.Count);
    }

    [Fact]
    public void MovingAwayInterruptsGathering()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        Begin(fixture, gathering, player, vein);
        var startX = player.Entity.Movement.State.PositionX;
        var startZ = player.Entity.Movement.State.PositionZ;

        Assert.Equal(
            MovementInputAcceptance.Accepted,
            player.Entity.Movement.AcceptInputs(new RealtimeMovementInputBatch(
                [new RealtimeMovementInput(1, 1, 1)],
                new RealtimeMovementPath(1, [new RealtimeMovementPathWaypoint(startX + 2f, 0f, startZ)]))));
        for (var tick = 0; tick < 10; tick++)
        {
            player.Entity.Movement.SimulateTick();
        }

        Assert.True(player.Entity.Movement.State.PositionX - startX > 0.1f);
        fixture.Clock.Advance();
        gathering.Process();

        Assert.Equal(0, gathering.ActiveCount);
        Assert.Equal(GatheringStopCodes.Interrupted, States(player).Last().Code);
        Assert.Equal(0, fixture.Completions.Count);
    }

    [Fact]
    public void ClosingTheInteractionStopsGathering()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        var start = Begin(fixture, gathering, player, vein);

        Assert.True(fixture.Leases.ReleaseWorldActor(player.Context.PeerId, start.InteractionSessionId));
        fixture.Clock.Advance();
        gathering.Process();

        Assert.Equal(0, gathering.ActiveCount);
        Assert.Equal(GatheringStopCodes.InteractionClosed, States(player).Last().Code);
    }

    [Fact]
    public void ACycleQueuedBehindOtherAuthorityWorkReleasesItsChargeWhenGatheringEnds()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        Begin(fixture, gathering, player, vein);
        player.Context.AuthorityOperationInFlight = true;

        for (var tick = 0; tick < 3 * fixture.Config.MovementSimulation.TickRateHz; tick++)
        {
            fixture.Clock.Advance();
            gathering.Process();
        }

        Assert.Equal(1, vein.ReservedResourceCharges);
        Assert.Single(player.Context.AuthorityOperationQueue);
        Assert.Equal(0, fixture.Completions.Count);

        gathering.Clear();

        Assert.Equal(0, vein.ReservedResourceCharges);
        Assert.Equal(0, gathering.ActiveCount);

        // The queued cycle no longer belongs to a gatherer, so starting it does nothing.
        player.Context.AuthorityOperationInFlight = false;
        fixture.Scheduler.StartNext(player.Context);
        Assert.Equal(0, fixture.Completions.Count);
        Assert.False(player.Context.AuthorityOperationInFlight);
        Assert.Equal(0, vein.ReservedResourceCharges);
    }

    [Fact]
    public void ASuccessfulStartGatheringResultBeginsGatheringAfterTheResult()
    {
        using var fixture = new RealtimeLoopFixture(enableWorldActors: true);
        var gathering = fixture.CreateGatheringHandler();
        var interactions = fixture.CreateWorldInteractionHandler(gathering);
        var player = fixture.ConnectPlayer(1);
        var vein = Vein(fixture);
        Assert.True(fixture.Leases.TryAcquireWorldActor(
            player.Context.PeerId,
            vein,
            out var lease,
            out _,
            out _));
        player.Context.AuthorityOperationInFlight = true;

        interactions.Complete(new WorldInteractionOperationCompleted(
            player.Context,
            player.Session,
            new WorldInteractionAuthorityResult(
                null,
                new RealtimeWorldInteractionResult(
                    Guid.NewGuid(),
                    lease!.InteractionSessionId,
                    RealtimeWorldInteractionOperationKind.CapabilityAction,
                    true,
                    vein.InteractionRevision,
                    0,
                    "Mine.",
                    null!),
                null,
                GatheringStart: new GatheringStart(lease.InteractionSessionId, CapabilityId, vein))));

        Assert.True(RealtimeProtocol.TryDecodeWorldInteractionResult(player.Peer.Sent[^2].Data, out var result, out _));
        Assert.True(result.Succeeded);
        Assert.Equal(RealtimeGatheringStateKind.Started, Assert.Single(States(player)).Kind);
        Assert.Equal(1, gathering.ActiveCount);
    }

    private static WorldActorRuntimeState Vein(RealtimeLoopFixture fixture)
    {
        return fixture.WorldActors!.ListActors()
            .Single(actor => actor.Definition.Kind == WorldActorKindIds.ResourceNode);
    }

    private static GatheringStart Begin(
        RealtimeLoopFixture fixture,
        GatheringHandler gathering,
        ConnectedPlayer player,
        WorldActorRuntimeState vein)
    {
        Assert.True(fixture.Leases.TryAcquireWorldActor(
            player.Context.PeerId,
            vein,
            out var lease,
            out _,
            out _));
        var start = new GatheringStart(lease!.InteractionSessionId, CapabilityId, vein);
        gathering.Begin(player.Peer, player.Context, player.Session, start);
        return start;
    }

    // Advances the clock until the next cycle reaches GameService and returns its completion.
    private static GatheringCycleCompleted RunCycle(
        RealtimeLoopFixture fixture,
        GatheringHandler gathering,
        out int ticks)
    {
        ticks = 0;
        while (fixture.Completions.Count == 0)
        {
            Assert.True(ticks < 10_000, "No gathering cycle started.");
            fixture.Clock.Advance();
            gathering.Process();
            ticks++;
        }

        Assert.True(fixture.Completions.TryDequeue(out var completion));
        Assert.Same(gathering, completion.Handler);
        var cycle = Assert.IsType<GatheringCycleCompleted>(completion.Operation);
        Assert.Equal("gathering_unavailable", cycle.Result.Error!.Code);
        return cycle;
    }

    private static GatheringCycleCompleted Yielded(
        GatheringCycleCompleted cycle,
        ConnectedPlayer player,
        long revision)
    {
        return cycle with
        {
            Result = new SimulationGatheringResult(
                new SimulationItemTransactionResponse(
                    cycle.Request.OperationId,
                    SimulationGatheringService.OperationKind,
                    true,
                    null,
                    [new SimulationItemCharacterRevision(player.Session.CharacterId, revision, 10, 200)],
                    [],
                    [],
                    [],
                    CharacterStateTestData.Create(player.Session.CharacterId, revision, 10)),
                null)
        };
    }

    private static GatheringCycleCompleted Failed(GatheringCycleCompleted cycle, string code)
    {
        return cycle with
        {
            Result = new SimulationGatheringResult(
                null,
                new SimulationWorkerErrorResponse(code, "Test outcome."))
        };
    }

    private static List<RealtimeGatheringState> States(ConnectedPlayer player)
    {
        var states = new List<RealtimeGatheringState>();
        foreach (var sent in player.Peer.Sent)
        {
            if (RealtimeProtocol.TryDecodeGatheringState(sent.Data, out var state, out _))
            {
                states.Add(state);
            }
        }

        return states;
    }
}
