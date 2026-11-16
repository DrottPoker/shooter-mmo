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
// the loop guard and is timed under its name: one that throws is logged and skipped for this
// iteration, and one that keeps failing ends the loop. Every component except the completion queue and the operation tracker
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
    // How long the stop notice and the disconnects that carry it may take before the transport
    // closes.
    private static readonly TimeSpan StopNoticeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly RealtimeLoopStep networkPollStep = loopGuard.LoopStep("network poll");
    private readonly RealtimeLoopStep completedOperationsStep = loopGuard.LoopStep("completed operations");
    private readonly RealtimeLoopStep peerSupervisionStep = loopGuard.LoopStep("peer supervision");
    private readonly RealtimeLoopStep orphanSweepStep = loopGuard.LoopStep("orphan sweep");
    private readonly RealtimeLoopStep sendFlushStep = loopGuard.LoopStep("send flush");
    private readonly RealtimePerformanceMetrics.DurationTimer networkPollTimer = performanceMetrics.LoopStepTimer("network poll");
    private readonly RealtimePerformanceMetrics.DurationTimer completedOperationsTimer = performanceMetrics.LoopStepTimer("completed operations");
    private readonly RealtimePerformanceMetrics.DurationTimer peerSupervisionTimer = performanceMetrics.LoopStepTimer("peer supervision");
    private readonly RealtimePerformanceMetrics.DurationTimer orphanSweepTimer = performanceMetrics.LoopStepTimer("orphan sweep");
    private readonly RealtimePerformanceMetrics.DurationTimer sendFlushTimer = performanceMetrics.LoopStepTimer("send flush");
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
        // would shift ticks, and lets it block in a precise timer wait. Its priority decides how
        // often other work on a busy machine preempts it.
        logger.LogInformation(
            "[SIMULATION] The realtime loop thread runs at priority {LoopThreadPriority} and measures its CPU time per tick with {CpuClock}.",
            config.LoopThreadPriority,
            tickLoop.CpuClock?.Description ?? "nothing, since the system offers no clock for it");
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
            Name = "SimulationWorker realtime loop",
            Priority = config.LoopThreadPriority
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

        var elapsed = Stopwatch.GetElapsedTime(phaseStarted);
        performanceMetrics.RecordNetworkPoll(elapsed);
        networkPollTimer.Record(elapsed);

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

        elapsed = Stopwatch.GetElapsedTime(phaseStarted);
        performanceMetrics.RecordCompletedOperations(elapsed);
        completedOperationsTimer.Record(elapsed);

        // Every tick phase runs under the guard inside the tick loop, which times them.
        tickLoop.ProcessDueTicks(
            simulationClock.Elapsed,
            simulationInterval,
            ref nextSimulationTick);
        phaseStarted = Stopwatch.GetTimestamp();
        try
        {
            supervisor.EnforcePeerState();
            peerSupervisionStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(peerSupervisionStep, exception);
        }

        peerSupervisionTimer.Record(Stopwatch.GetElapsedTime(phaseStarted));
        phaseStarted = Stopwatch.GetTimestamp();
        try
        {
            supervisor.ReleaseOrphanedSessions();
            orphanSweepStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(orphanSweepStep, exception);
        }

        orphanSweepTimer.Record(Stopwatch.GetElapsedTime(phaseStarted));

        // The transport otherwise sends queued packets only on its own 15 ms update, which
        // delayed every snapshot by a random part of that period.
        phaseStarted = Stopwatch.GetTimestamp();
        try
        {
            transport.FlushSends();
            sendFlushStep.Succeeded();
        }
        catch (Exception exception)
        {
            loopGuard.Failed(sendFlushStep, exception);
        }

        sendFlushTimer.Record(Stopwatch.GetElapsedTime(phaseStarted));
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

    // Sends every connected peer a ServerDisconnect with the reason of the stop, then, once
    // LiteNetLib sent it, disconnects every peer with the same notice as the disconnect's data,
    // or with the reason the peer was already going for, and waits until every client confirmed
    // its disconnect. LiteNetLib sends a disconnect again every 300 ms until then, so a peer that
    // lost the reliable notice still learns why. All of it takes at most StopNoticeTimeout; a peer
    // whose disconnect is not confirmed by then sees only the closed connection. The loop has
    // ended, so nothing else uses the transport.
    private async Task TellPeersAsync()
    {
        var told = new List<(IRealtimePeerConnection Peer, byte[] Data)>();
        try
        {
            var packet = RealtimeProtocol.EncodeServerDisconnect(stopReason.Code, stopReason.Message);
            var notice = RealtimeDisconnectData.Fit(packet);
            foreach (var context in peers.Contexts)
            {
                if (peers.TryGetCurrentPeer(context, out var peer))
                {
                    sender.SendControl(peer, packet);
                    told.Add((peer, context.PendingDisconnect?.Data ?? notice));
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

            // An empty outgoing queue of the notice's channel means that LiteNetLib sent the notice
            // once, not that the client acknowledged it; a disconnect stops the resends of the
            // reliable packets of every channel. The other channels need not wait: the session
            // ends with the notice.
            var noticeChannel = RealtimeReliableChannels.Of(packet);
            while (Stopwatch.GetElapsedTime(started) < StopNoticeTimeout
                && told.Exists(entry => entry.Peer.GetPacketsCountInReliableQueue(noticeChannel, true) > 0))
            {
                await Task.Delay(10);
                transport.FlushSends();
            }

            foreach (var (peer, data) in told)
            {
                transport.DisconnectPeer(peer, data);
            }

            while (Stopwatch.GetElapsedTime(started) < StopNoticeTimeout
                && told.Exists(entry => transport.AwaitsDisconnectConfirmation(entry.Peer)))
            {
                await Task.Delay(10);
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
