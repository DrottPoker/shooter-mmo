using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.Server.Common.Timing;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the realtime loop thread. Each iteration polls the transport, drains completed
// operations within a budget, runs due simulation ticks, supervises peers and hands the
// queued packets to the transport, in that order. The loop then sleeps precisely until the
// next tick or poll is due, so ticks and snapshots keep an even rhythm. Each step runs under
// the loop guard: one that throws is logged and skipped for this iteration, and one that keeps
// failing ends the loop. Every component except the completion queue and the operation tracker
// is confined to this thread, so none of them needs a lock.
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
    RealtimePerformanceMetrics performanceMetrics,
    RealtimeFeatureRegistries registries,
    RealtimeLoopGuard loopGuard,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    GameServiceAvailability availability,
    SimulationWorkerStopReason stopReason,
    IReadOnlyList<IDisposable> ownedResources,
    ILogger logger) : IDisposable
{
    // How long the stop notice may take to be acknowledged before the transport closes.
    private static readonly TimeSpan StopNoticeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly RealtimeLoopStep networkPollStep = loopGuard.LoopStep("network poll");
    private readonly RealtimeLoopStep completedOperationsStep = loopGuard.LoopStep("completed operations");
    private readonly RealtimeLoopStep peerSupervisionStep = loopGuard.LoopStep("peer supervision");
    private readonly RealtimeLoopStep orphanSweepStep = loopGuard.LoopStep("orphan sweep");
    private readonly RealtimeLoopStep sendFlushStep = loopGuard.LoopStep("send flush");
    private bool disposed;

    // What the features registered; the guard tests read it.
    public RealtimeFeatureRegistries Registries => registries;

    public IReadOnlyList<SimulationTickPhase> TickPhases => tickLoop.Phases;

    // Counts the failed steps and isolated players and peers; the test harness reads it.
    public RealtimeLoopGuard LoopGuard => loopGuard;

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
        try
        {
            transport.PollEvents();
            networkPollStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(networkPollStep, exception);
        }

        performanceMetrics.RecordNetworkPoll(
            Stopwatch.GetElapsedTime(phaseStarted));

        phaseStarted = Stopwatch.GetTimestamp();
        try
        {
            ProcessCompletedOperations(completedOperationBudget);
            completedOperationsStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(completedOperationsStep, exception);
        }

        performanceMetrics.RecordCompletedOperations(
            Stopwatch.GetElapsedTime(phaseStarted));

        // Every tick phase runs under the guard inside the tick loop.
        tickLoop.ProcessDueTicks(
            simulationClock.Elapsed,
            simulationInterval,
            ref nextSimulationTick);
        try
        {
            supervisor.EnforcePeerState();
            peerSupervisionStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(peerSupervisionStep, exception);
        }

        try
        {
            supervisor.ReleaseOrphanedSessions();
            orphanSweepStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(orphanSweepStep, exception);
        }

        // The transport otherwise sends queued packets only on its own 15 ms update, which
        // delayed every snapshot by a random part of that period.
        try
        {
            transport.FlushSends();
            sendFlushStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(sendFlushStep, exception);
        }
    }

    private void ProcessCompletedOperations(TimeSpan completedOperationBudget)
    {
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
    }

    // The order is part of the shutdown contract: every connected peer is told why the worker
    // stops, then the transport stops, so no event can register state, and in-flight operations
    // finish before the registered shutdown steps flush and clear, in the order of
    // RealtimeShutdownOrder, so a late join or release cannot register state after the clear.
    // Work that waits for GameService gives up first, so it keeps nothing waiting. Their
    // completions are never finalized; only the combat flush reads its own arrow batch results.
    private async Task ShutdownAsync()
    {
        await TellPeersAsync();
        transport.Stop();
        availability.EndWaits();
        await operationTracker.WaitForAllAsync();
        foreach (var step in registries.ShutdownSteps.Steps)
        {
            await step.Run();
        }

        logger.LogInformation("[SIMULATION] Realtime transport stopped.");
    }

    // Sends every connected peer a ServerDisconnect with the reason of the stop and gives the
    // reliable packets a short time to be acknowledged; a peer that does not acknowledge in time
    // sees only the closed connection. The loop has ended, so nothing else uses the transport.
    private async Task TellPeersAsync()
    {
        var told = new List<IRealtimePeerConnection>();
        try
        {
            var packet = RealtimeProtocol.EncodeServerDisconnect(stopReason.Code, stopReason.Message);
            foreach (var context in peers.Contexts)
            {
                if (peers.TryGetCurrentPeer(context, out var peer))
                {
                    sender.SendControl(peer, packet);
                    told.Add(peer);
                }
            }

            if (told.Count == 0)
            {
                return;
            }

            logger.LogInformation(
                "[SIMULATION] Telling {PeerCount} connected peers that the worker stops with {Code}.",
                told.Count,
                stopReason.Code);
            var started = Stopwatch.GetTimestamp();
            transport.FlushSends();
            while (Stopwatch.GetElapsedTime(started) < StopNoticeTimeout
                && told.Exists(peer => peer.GetPacketsCountInReliableQueue(RealtimeProtocol.ControlChannel, true) > 0))
            {
                await Task.Delay(10);
                transport.FlushSends();
            }
        }
        catch (Exception exception)
        {
            // The stop goes on without the notice.
            logger.LogWarning(
                exception,
                "[SIMULATION] Could not tell the connected peers that the worker stops.");
        }
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
