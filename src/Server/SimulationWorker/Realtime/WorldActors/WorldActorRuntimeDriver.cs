using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Activates the worker's world actor assignment and evaluates world actor activity on
// each simulation tick. Without a world actor store both steps do nothing. Loop-confined.
internal sealed class WorldActorRuntimeDriver(
    SimulationWorkerConfig config,
    WorldActorStore? worldActorStore,
    WorldActorActivityScheduler? worldActorActivityScheduler,
    WorldActorMetrics? worldActorMetrics,
    SimulationWorkerIdentity? workerIdentity,
    EntityPresenceReplicator presence,
    RealtimeNetworkMetrics networkMetrics,
    ILogger logger)
{
    public void Activate()
    {
        if (worldActorStore is not null)
        {
            if (workerIdentity is null)
            {
                throw new InvalidOperationException(
                    "World actor runtime requires the current SimulationWorker identity.");
            }

            var actors = worldActorStore.ActivateAssignment(
                config.WorldId,
                config.ShardId,
                workerIdentity.RuntimeId);
            worldActorMetrics?.ObservePopulation(actors);
            presence.RebuildInterestIndex(Array.Empty<PlayerSimulationEntity>());
            networkMetrics.SetActiveEntities(presence.ActiveEntityCount);
            logger.LogInformation(
                "[SIMULATION] Activated {ActorCount} world actors from revision {WorldActorRevision} for worker runtime {RuntimeId}.",
                actors.Count,
                worldActorStore.ContentRevision,
                workerIdentity.RuntimeId);
        }
    }

    public void EvaluateTick(IReadOnlyList<PlayerSimulationEntity> players, uint serverTick)
    {
        if (worldActorActivityScheduler is not null)
        {
            foreach (var changedActor in worldActorActivityScheduler.Evaluate(
                         players,
                         serverTick,
                         config.MovementSimulation.TickRateHz))
            {
                presence.BroadcastWorldActorState(changedActor);
            }

            if (worldActorStore is not null)
            {
                worldActorMetrics?.ObservePopulation(worldActorStore.ListActors());
            }
        }
    }
}
