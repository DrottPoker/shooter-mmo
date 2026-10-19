using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs due fixed simulation ticks in a stable phase order: collision streaming, carry
// state and movement per player in entity id order, world actor activity, combat,
// interaction revalidation, snapshots and corpse presence. At most five ticks catch up per call
// before the clock resynchronizes. Loop-confined.
internal sealed class SimulationTickLoop(
    SimulationWorkerConfig config,
    RealtimeSimulationClock clock,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    WorldActorRuntimeDriver worldActors,
    WorldInteractionHandler worldInteractions,
    GatheringHandler gathering,
    CombatSystem combat,
    SnapshotReplicator snapshots,
    CorpsePresenceReplicator corpsePresence,
    WorldCollisionStreamingStore? collisionStreamingStore,
    RealtimePerformanceMetrics performanceMetrics,
    ILogger logger)
{
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<SimulationVector3> collisionAnchorBuffer = [];

    public void ProcessDueTicks(
        TimeSpan elapsed,
        TimeSpan simulationInterval,
        ref TimeSpan nextSimulationTick)
    {
        const int maximumCatchUpTicks = 5;
        var processedTicks = 0;

        while (elapsed >= nextSimulationTick && processedTicks < maximumCatchUpTicks)
        {
            var tickStarted = Stopwatch.GetTimestamp();
            performanceMetrics.RecordSimulationTickLag(elapsed - nextSimulationTick);
            clock.Advance();

            var phaseStarted = Stopwatch.GetTimestamp();
            RefreshCollisionStreaming();
            performanceMetrics.RecordCollisionStreaming(
                Stopwatch.GetElapsedTime(phaseStarted));

            phaseStarted = Stopwatch.GetTimestamp();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                carrySync.ApplyLatest(entity);
                entity.Movement.SimulateTick();
            }
            performanceMetrics.RecordMovementSimulation(
                Stopwatch.GetElapsedTime(phaseStarted));

            worldActors.EvaluateTick(playerBuffer, clock.ServerTick);

            combat.ProcessTick();

            var interactionValidationIntervalTicks = Math.Max(
                1,
                config.MovementSimulation.TickRateHz / 5);
            if (worldInteractions.IsActive
                && clock.ServerTick % interactionValidationIntervalTicks == 0)
            {
                worldInteractions.RevalidateLeases();
            }

            gathering.Process();

            var snapshotIntervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
            if (clock.ServerTick % snapshotIntervalTicks == 0)
            {
                snapshots.Broadcast();
            }

            var corpsePresenceIntervalTicks = Math.Max(
                1,
                config.MovementSimulation.TickRateHz / 2);
            if (clock.ServerTick % corpsePresenceIntervalTicks == 0)
            {
                corpsePresence.Broadcast();
            }

            performanceMetrics.RecordSimulationTick(
                Stopwatch.GetElapsedTime(tickStarted),
                simulationInterval);
            nextSimulationTick += simulationInterval;
            processedTicks++;
        }

        if (processedTicks == maximumCatchUpTicks && elapsed >= nextSimulationTick)
        {
            logger.LogWarning(
                "[SIMULATION] Movement simulation exceeded its catch-up budget at server tick {ServerTick}. Resynchronizing the fixed-tick clock.",
                clock.ServerTick);
            performanceMetrics.RecordTickResynchronization();
            nextSimulationTick = elapsed + simulationInterval;
        }
    }

    private void RefreshCollisionStreaming()
    {
        if (collisionStreamingStore is null)
        {
            return;
        }

        entityRegistry.CopyPlayersTo(playerBuffer);
        collisionAnchorBuffer.Clear();
        foreach (var entity in playerBuffer)
        {
            collisionAnchorBuffer.Add(new SimulationVector3(
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionY,
                entity.Movement.State.PositionZ));
        }

        collisionAnchorBuffer.Add(new SimulationVector3(
            config.MovementSpawn.X,
            config.MovementSpawn.Y,
            config.MovementSpawn.Z));
        collisionStreamingStore.Refresh(collisionAnchorBuffer);
    }
}
