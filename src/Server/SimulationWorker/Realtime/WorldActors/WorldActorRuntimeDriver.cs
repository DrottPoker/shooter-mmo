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
    WorldActorTimerQueue timers,
    ILogger logger)
{
    private readonly List<WorldActorTimer> dueTimers = [];

    // Respawns a dead Mob when its respawn timer is due; set by the composition.
    public Action<WorldActorTimer>? RespawnMob { get; set; }

    // Hears the actors of an activated assignment, such as combat registering its Mobs.
    public Action<IReadOnlyList<WorldActorRuntimeState>>? Activated { get; set; }

    // Keeps a Mob active while it is busy, such as fighting or walking home.
    public Func<WorldActorRuntimeState, bool>? KeepActive { get; set; }

    public void Activate()
    {
        timers.Clear();
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
            Activated?.Invoke(actors);
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
        EvaluateTick(PlayerSpatialGrid.From(players), serverTick);
    }

    public void EvaluateTick(PlayerSpatialGrid players, uint serverTick)
    {
        RespawnDueResourceNodes(serverTick);
        if (worldActorActivityScheduler is not null)
        {
            foreach (var changedActor in worldActorActivityScheduler.Evaluate(
                         players,
                         serverTick,
                         config.MovementSimulation.TickRateHz,
                         KeepActive))
            {
                presence.BroadcastWorldActorState(changedActor);
            }

            if (worldActorStore is not null)
            {
                worldActorMetrics?.ObservePopulation(worldActorStore.ListActors());
            }
        }
    }

    // A depleted resource node returns with full charges once its respawn delay passed.
    // A timer from an older depletion or another worker runtime finds no matching node.
    private void RespawnDueResourceNodes(uint serverTick)
    {
        if (worldActorStore is null || timers.Count == 0)
        {
            return;
        }

        timers.DrainDue(serverTick, dueTimers);
        foreach (var timer in dueTimers)
        {
            if (timer.Kind == WorldActorTimerKind.MobRespawn)
            {
                RespawnMob?.Invoke(timer);
                continue;
            }

            if (worldActorStore.TryGetByRuntimeId(timer.RuntimeActorId, out var actor)
                && actor!.ReplenishResource(timer.Generation))
            {
                presence.BroadcastWorldActorState(actor);
            }
        }
    }
}
