using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the realtime loop strand. Each iteration polls the transport, drains completed
// operations within a budget, runs due simulation ticks and supervises peers, in that
// order. Every component except the completion queue and the operation tracker is
// confined to this loop, so none of them needs a lock.
internal sealed class RealtimeServer(
    SimulationWorkerConfig config,
    LiteNetLibRealtimeTransport transport,
    RealtimePeerLifecycle lifecycle,
    RealtimeMessageDispatcher dispatcher,
    RealtimeCompletionQueue completions,
    RealtimeCompletionProcessor completionProcessor,
    RealtimeOperationTracker operationTracker,
    CorpseMutationCoordinator corpseMutationCoordinator,
    SimulationTickLoop tickLoop,
    RealtimePeerSupervisor supervisor,
    WorldActorRuntimeDriver worldActors,
    RealtimeTransportReadiness transportReadiness,
    RealtimePeerRegistry peers,
    EntityPresenceReplicator presence,
    ConnectionEntityBindingRegistry connectionBindings,
    SimulationEntityRegistry entityRegistry,
    CarryStateStore carryStateStore,
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    WorldActorStore? worldActorStore,
    CorpseViewDistributor corpseViews,
    RealtimeNetworkMetrics networkMetrics,
    RealtimePerformanceMetrics performanceMetrics,
    IReadOnlyList<IDisposable> ownedResources,
    ILogger logger) : IDisposable
{
    private bool disposed;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        worldActors.Activate();

        transport.Start(lifecycle, dispatcher);

        transportReadiness.MarkListening(config.UdpPort);

        logger.LogInformation(
            "[SIMULATION] Realtime transport listening on UDP port {UdpPort} with protocol version {ProtocolVersion}, {TickRate} simulation ticks per second, and {SnapshotRate} snapshots per second.",
            config.UdpPort,
            RealtimeProtocol.Version,
            config.MovementSimulation.TickRateHz,
            config.SnapshotRateHz);

        var simulationClock = Stopwatch.StartNew();
        var simulationInterval = TimeSpan.FromSeconds(
            1d / config.MovementSimulation.TickRateHz);
        var completedOperationBudget = TimeSpan.FromTicks(
            Math.Max(1, simulationInterval.Ticks / 8));
        var nextSimulationTick = simulationClock.Elapsed + simulationInterval;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RunIteration(
                    simulationClock,
                    simulationInterval,
                    completedOperationBudget,
                    ref nextSimulationTick);
                await Task.Delay(config.NetworkPollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private void RunIteration(
        Stopwatch simulationClock,
        TimeSpan simulationInterval,
        TimeSpan completedOperationBudget,
        ref TimeSpan nextSimulationTick)
    {
        var phaseStarted = Stopwatch.GetTimestamp();
        transport.PollEvents();
        performanceMetrics.RecordNetworkPoll(
            Stopwatch.GetElapsedTime(phaseStarted));

        phaseStarted = Stopwatch.GetTimestamp();
        performanceMetrics.ObserveCompletedOperationBacklog(
            completions.Count);
        performanceMetrics.ObserveCorpseMutationCoordinator(
            corpseMutationCoordinator.WaitingMutationCount,
            corpseMutationCoordinator.ActiveCorpseCount);
        completionProcessor.ProcessCompleted(completedOperationBudget);
        performanceMetrics.ObserveCompletedOperationBacklog(
            completions.Count);
        performanceMetrics.ObserveCorpseMutationCoordinator(
            corpseMutationCoordinator.WaitingMutationCount,
            corpseMutationCoordinator.ActiveCorpseCount);
        performanceMetrics.RecordCompletedOperations(
            Stopwatch.GetElapsedTime(phaseStarted));
        tickLoop.ProcessDueTicks(
            simulationClock.Elapsed,
            simulationInterval,
            ref nextSimulationTick);
        supervisor.EnforcePeerState();
        supervisor.ReleaseOrphanedSessions();
    }

    // The order is part of the shutdown contract: the transport stops first, so no event
    // can register state, and in-flight operations finish before the stores are cleared,
    // so a late join or release cannot register state after the clear. Their completions
    // are never finalized.
    private async Task ShutdownAsync()
    {
        transport.Stop();
        await operationTracker.WaitForAllAsync();
        peers.Clear();
        presence.Clear();
        connectionBindings.Clear();
        entityRegistry.Clear();
        carryStateStore.Clear();
        corpseViewers.Clear();
        worldInteractionLeases?.Clear();
        worldActorStore?.Clear();
        corpseViews.ClearSnapshots();
        supervisor.ClearUnboundSessions();
        networkMetrics.SetActivePeers(0);
        networkMetrics.SetActiveEntities(0);
        networkMetrics.SetPeerPopulation(0, 0, 0);
        logger.LogInformation("[SIMULATION] Realtime transport stopped.");
    }

    // Disposes only what the composition created; provided services stay with the host.
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var resource in ownedResources)
        {
            resource.Dispose();
        }
    }
}
