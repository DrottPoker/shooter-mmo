using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.Server.Common.Timing;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the realtime loop thread. Each iteration polls the transport, drains completed
// operations within a budget, runs due simulation ticks, supervises peers and hands the
// queued packets to the transport, in that order. The loop then sleeps precisely until the
// next tick or poll is due, so ticks and snapshots keep an even rhythm. Every component
// except the completion queue and the operation tracker is confined to this thread, so none
// of them needs a lock.
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
    CombatSystem combat,
    PlayerCombatAuthority combatAuthority,
    RealtimeTransportReadiness transportReadiness,
    RealtimePeerRegistry peers,
    EntityPresenceReplicator presence,
    ConnectionEntityBindingRegistry connectionBindings,
    SimulationEntityRegistry entityRegistry,
    CharacterItemStateStore itemStateStore,
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

        // A dedicated thread keeps the loop off the thread pool, whose scheduling delays
        // would shift ticks, and lets it block in a precise timer wait.
        var loopCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loopThread = new Thread(() =>
        {
            try
            {
                RunLoop(stoppingToken);
                loopCompleted.SetResult();
            }
            catch (Exception exception)
            {
                loopCompleted.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SimulationWorker realtime loop"
        };
        try
        {
            loopThread.Start();
            await loopCompleted.Task;
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        using var loopTimer = new PreciseLoopTimer();
        if (!loopTimer.IsHighResolution)
        {
            logger.LogWarning(
                "[SIMULATION] No high-resolution timer is available, so loop waits round up to the system timer tick and ticks run late.");
        }

        var simulationClock = Stopwatch.StartNew();
        var simulationInterval = TimeSpan.FromSeconds(
            1d / config.MovementSimulation.TickRateHz);
        var completedOperationBudget = TimeSpan.FromTicks(
            Math.Max(1, simulationInterval.Ticks / 8));
        var nextSimulationTick = simulationClock.Elapsed + simulationInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            RunIteration(
                simulationClock,
                simulationInterval,
                completedOperationBudget,
                ref nextSimulationTick);
            var now = simulationClock.Elapsed;
            var nextPoll = now + config.NetworkPollInterval;
            var wakeAt = nextSimulationTick < nextPoll ? nextSimulationTick : nextPoll;
            if (!loopTimer.Wait(wakeAt - now, stoppingToken))
            {
                return;
            }
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

        // The transport otherwise sends queued packets only on its own 15 ms update, which
        // delayed every snapshot by a random part of that period.
        transport.FlushSends();
    }

    // The order is part of the shutdown contract: the transport stops first, so no event
    // can register state, and in-flight operations finish before the stores are cleared,
    // so a late join or release cannot register state after the clear. Their completions
    // are never finalized; only the combat flush reads its own arrow batch results.
    private async Task ShutdownAsync()
    {
        transport.Stop();
        await operationTracker.WaitForAllAsync();
        await combatAuthority.FlushOnShutdownAsync(completions.TakeFor(combatAuthority));
        combat.Clear();
        peers.Clear();
        presence.Clear();
        connectionBindings.Clear();
        entityRegistry.Clear();
        itemStateStore.Clear();
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
