using System.Diagnostics;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.StackStressGenerator;

// Runs one stress run: admission in ramp steps, the steady interval, the drain, and the leave.
// The bots are polled on dedicated poll threads, StressPollGroup, and this loop only
// coordinates; the process and PostgreSQL samplers run on their own loop, so neither can make
// a poll round late.
public sealed class StressRunCoordinator(
    StressGeneratorOptions options,
    IStressAdmissionProvider admissionProvider)
{
    // Bots poll and send due inputs this often, about the frame time of a 300 FPS client.
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(3);

    // The coordination loop needs no frame precision.
    private static readonly TimeSpan CoordinationInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly List<IStressBot> bots = [];
    private readonly List<StressPollGroup> groups = [];
    private readonly List<StressBotSnapshot> admissionFailures = [];
    private readonly List<PendingAdmission> pendingAdmissions = [];
    private readonly HashSet<int> admittedBotIndexes = [];
    private readonly object timingGate = new();
    private readonly StressLatencyAccumulator inputAcknowledgementLatencies = new(options.Seed);
    private readonly StressLatencyAccumulator intervalInputAcknowledgementLatencies =
        new(unchecked(options.Seed + 1));
    private readonly StressLatencyAccumulator pollLateness = new(unchecked(options.Seed + 2));
    private readonly StressLatencyAccumulator samplingSteps = new(unchecked(options.Seed + 3));
    private readonly StressGameplayMetrics gameplayMetrics = new(options.Seed);
    private readonly SemaphoreSlim samplingGate = new(1, 1);
    private readonly Dictionary<int, (PlayingStressBot Bot, StressPollGroup Group)> playingBots = [];
    private PlayingRunContext? playing;
    private IReadOnlyList<PlayingChurnEvent> churnSchedule = [];
    private int nextChurnEvent;
    private int launchedBots;
    private volatile int joinedBotsForDrift;
    private volatile StressDriftSample? latestDrift;
    private long nextDriftTimestamp;

    public async Task<StressRunReport> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"Waiting up to {options.WorkerWaitTimeout.TotalSeconds:0} seconds for {StressGeneratorOptions.FormatMode(options.Mode)} readiness.");
        var target = await admissionProvider.WaitForReadyAsync(
            options.WorkerWaitTimeout,
            cancellationToken);
        Console.WriteLine(
            $"Stress target shard {target.ShardId}, World {target.WorldId}, capacity {target.Capacity} is ready.");
        playing = options.Workload == StressWorkloadProfile.Playing
            ? CreatePlayingContext()
            : null;
        await admissionProvider.PrepareAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        using var processSampler = target.Worker is not null
            ? StressProcessSampler.TryAttach(target.Worker.StartedAt)
            : StressProcessSampler.TryAttach("Mmorpg.SimulationWorker");
        using var generatorProcessSampler = StressProcessSampler.AttachCurrent();
        processSampler?.Sample();
        generatorProcessSampler.Sample();
        if (options.BotCount > target.Capacity)
        {
            Console.WriteLine(
                $"Warning: requested {options.BotCount} bots exceeds the target capacity {target.Capacity}. Rejections are expected.");
        }

        using var samplingStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        for (var index = 0; index < options.PollThreads; index++)
        {
            groups.Add(new StressPollGroup(index, PollInterval, RecordPollLateness, cancellationToken));
        }

        var sampling = SampleLoopAsync(processSampler, generatorProcessSampler, samplingStop.Token);
        var startedAt = DateTime.UtcNow;
        var startedTimestamp = Stopwatch.GetTimestamp();
        var nextRampTimestamp = startedTimestamp;
        var nextReportTimestamp = AddDuration(startedTimestamp, options.ReportInterval);
        long? steadyDeadlineTimestamp = null;
        var steadyStartedTimestamp = 0L;
        var nextJoinedCountTimestamp = startedTimestamp;
        DateTime? steadyStartedAt = null;
        var rampComplete = false;
        nextDriftTimestamp = AddDuration(startedTimestamp, options.ReportInterval);

        try
        {
            while (!steadyDeadlineTimestamp.HasValue
                || Stopwatch.GetTimestamp() < steadyDeadlineTimestamp.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowOnFailure(sampling);
                var now = Stopwatch.GetTimestamp();
                await CompleteAdmissionsAsync(cancellationToken);

                if (launchedBots < options.BotCount && now >= nextRampTimestamp)
                {
                    LaunchRampStep(cancellationToken);
                    nextRampTimestamp = AddDuration(now, options.RampInterval);
                    if (launchedBots == options.BotCount)
                    {
                        rampComplete = true;
                        Console.WriteLine(
                            $"All {options.BotCount} bot lifecycles started. Waiting for admission and UDP join attempts to finish before steady state.");
                    }
                }

                if (rampComplete
                    && !steadyDeadlineTimestamp.HasValue
                    && pendingAdmissions.Count == 0
                    && bots.All(bot => bot.State is StressBotState.Joined or StressBotState.Failed))
                {
                    var joined = bots.Count(bot => bot.State == StressBotState.Joined);
                    joinedBotsForDrift = joined;
                    await admissionProvider.CaptureSteadyBoundaryAsync(true, joined, cancellationToken);
                    now = Stopwatch.GetTimestamp();
                    await samplingGate.WaitAsync(cancellationToken);
                    try
                    {
                        processSampler?.MarkSteadyState(options.SteadyDuration);
                        generatorProcessSampler.MarkSteadyState(options.SteadyDuration);
                        admissionProvider.MarkSteadyState(options.SteadyDuration);
                    }
                    finally
                    {
                        samplingGate.Release();
                    }

                    steadyStartedAt = DateTime.UtcNow;
                    steadyStartedTimestamp = now;
                    playing?.Metrics.BeginSteadyMeasurement();
                    foreach (var group in groups)
                    {
                        group.BeginSteadyMeasurement();
                        group.ForEachBot((bot, timestamp) => bot.BeginRemoteCadenceWindow(timestamp));
                    }

                    steadyDeadlineTimestamp = joined == 0
                        ? now
                        : AddDuration(now, options.SteadyDuration);
                    ScheduleChurn();
                    Console.WriteLine(
                        $"Admission complete with {joined}/{options.BotCount} bots joined and {admissionFailures.Count} HTTP admission failures. Beginning {(joined == 0 ? 0 : options.SteadyDuration.TotalSeconds):0}-second steady interval on {groups.Count} poll thread{(groups.Count == 1 ? string.Empty : "s")}.");
                }

                if (steadyDeadlineTimestamp.HasValue)
                {
                    DispatchChurn(now, steadyStartedTimestamp);
                }

                if (now >= nextJoinedCountTimestamp)
                {
                    joinedBotsForDrift = bots.Count(bot => bot.State == StressBotState.Joined);
                    nextJoinedCountTimestamp = AddDuration(now, SampleInterval);
                }

                if (now >= nextReportTimestamp)
                {
                    WriteProgress();
                    nextReportTimestamp = AddDuration(now, options.ReportInterval);
                }

                await Task.Delay(CoordinationInterval, cancellationToken);
            }

            var steadyEndedAt = DateTime.UtcNow;
            playing?.Metrics.EndSteadyMeasurement();
            foreach (var group in groups)
            {
                group.EndSteadyMeasurement();
                group.ForEachBot((bot, timestamp) => bot.EndRemoteCadenceWindow(timestamp));
            }

            WriteProgress();
            await samplingStop.CancelAsync();
            await StopSamplingAsync(sampling);
            processSampler?.Sample();
            generatorProcessSampler.Sample();
            await admissionProvider.SampleAsync(cancellationToken);
            processSampler?.MarkSteadyEnd();
            generatorProcessSampler.MarkSteadyEnd();
            admissionProvider.MarkSteadyEnd();
            await admissionProvider.CaptureSteadyBoundaryAsync(false, joinedBotsForDrift, cancellationToken);
            await DrainGameplayAsync(cancellationToken);
            await LeaveAsync(cancellationToken);
            StopPollThreads();
            await admissionProvider.CompleteAsync(
                admittedBotIndexes.ToArray(),
                cancellationToken);
            var endedAt = DateTime.UtcNow;
            var snapshots = bots
                .Select(bot => bot.Capture())
                .Concat(admissionFailures)
                .OrderBy(bot => bot.BotIndex)
                .ToArray();
            var providerReport = admissionProvider.CaptureReport();
            var workerProcess = processSampler?.CreateSummary();
            var generatorProcess = generatorProcessSampler.CreateSummary();
            return StressRunReport.Create(
                startedAt,
                endedAt,
                options,
                target,
                providerReport,
                workerProcess,
                generatorProcess,
                snapshots,
                inputAcknowledgementLatencies.CaptureSummary(),
                gameplayMetrics.Capture(
                    options.Workload,
                    providerReport.FullStack?.LootHotspotCorpseId)) with
            {
                SteadyStartedAt = steadyStartedAt,
                SteadyEndedAt = steadyEndedAt,
                Playing = playing?.Metrics.Capture(),
                Soak = new StressSoakSummary(
                    providerReport.Storage,
                    providerReport.Drift,
                    new[] { workerProcess?.Memory, providerReport.GameServiceProcess?.Memory, generatorProcess?.Memory }
                        .OfType<StressMemoryTrend>()
                        .ToArray()),
                GeneratorTiming = new StressGeneratorTimingSummary(
                    PollInterval.TotalMilliseconds,
                    pollLateness.CaptureSummary(),
                    playing?.Metrics.CaptureTiming(PlayingMetrics.RouteQueue),
                    playing is not null)
                {
                    PollThreads = options.PollThreads,
                    SamplingStep = samplingSteps.CaptureSummary()
                }
            };
        }
        finally
        {
            try
            {
                await samplingStop.CancelAsync();
                await StopSamplingAsync(sampling);
            }
            finally
            {
                // A sampler that failed, for example on a dropped database connection, rethrows
                // above. The poll threads run on the run's own token, so without this they
                // would keep polling bots and sending traffic until the process ends.
                StopPollThreads();
                foreach (var bot in bots)
                {
                    bot.Dispose();
                }

                playing?.Navigation?.Dispose();
            }
        }
    }

    // The process samplers and the provider's PostgreSQL sample run here once a second, apart
    // from the poll threads. How long each step took is recorded, because the steps once ran
    // inside the poll loop and made single rounds late.
    private async Task SampleLoopAsync(
        StressProcessSampler? workerSampler,
        StressProcessSampler generatorSampler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SampleInterval, cancellationToken);
                await samplingGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var started = Stopwatch.GetTimestamp();
                workerSampler?.Sample();
                generatorSampler.Sample();
                await admissionProvider.SampleAsync(cancellationToken);
                lock (timingGate)
                {
                    samplingSteps.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                // The drift checks run once a report interval, apart from the step that the
                // sampling timing measures.
                if (Stopwatch.GetTimestamp() >= nextDriftTimestamp)
                {
                    nextDriftTimestamp = AddDuration(Stopwatch.GetTimestamp(), options.ReportInterval);
                    latestDrift = await admissionProvider.SampleDriftAsync(joinedBotsForDrift, cancellationToken) ?? latestDrift;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                samplingGate.Release();
            }
        }
    }

    private static async Task StopSamplingAsync(Task sampling)
    {
        try
        {
            await sampling;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ThrowOnFailure(Task sampling)
    {
        if (sampling.IsFaulted)
        {
            throw new InvalidOperationException(
                "The stress samplers failed: " + sampling.Exception!.GetBaseException().Message,
                sampling.Exception.GetBaseException());
        }

        var failed = groups.FirstOrDefault(group => group.Failure is not null);
        if (failed is not null)
        {
            throw new InvalidOperationException(
                "A poll thread failed: " + failed.Failure!.Message,
                failed.Failure);
        }
    }

    private void StopPollThreads()
    {
        foreach (var group in groups)
        {
            group.Dispose();
        }

        groups.Clear();
    }

    private void RecordPollLateness(double milliseconds)
    {
        lock (timingGate)
        {
            pollLateness.Record(milliseconds);
        }
    }

    private void RecordInputAcknowledgement(double milliseconds)
    {
        lock (timingGate)
        {
            inputAcknowledgementLatencies.Record(milliseconds);
            intervalInputAcknowledgementLatencies.Record(milliseconds);
        }
    }

    // The playing bots share one navigation on the World's collision, the item and actor
    // catalogs they read like a client, and their assignments. Every home ground must have
    // ground under its center, which a run checks before the first bot joins.
    private PlayingRunContext CreatePlayingContext()
    {
        var scenario = options.Playing
            ?? throw new InvalidOperationException("The playing workload needs its scenario.");
        var metrics = new PlayingMetrics(options.Seed);
        SimulationBotNavigation navigation;
        try
        {
            navigation = SimulationBotNavigation.Load(
                options.WorldDataPath,
                options.WorldId,
                Math.Clamp(Environment.ProcessorCount / 4, 1, 4),
                timing => metrics.RecordRouteTiming(timing));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(
                $"The playing bots need the collision of World {options.WorldId} below '{options.WorldDataPath}': {exception.Message}",
                exception);
        }

        var buffer = new CollisionQueryBuffer();
        foreach (var ground in scenario.Grounds)
        {
            if (!CollisionRaycast.TryRaycast(
                    navigation.CollisionWorld,
                    new SimulationVector3(ground.Area.CenterX, 100f, ground.Area.CenterZ),
                    new SimulationVector3(0f, -1f, 0f),
                    200f,
                    CollisionLayers.CharacterMovement,
                    buffer,
                    out _))
            {
                navigation.Dispose();
                throw new InvalidOperationException(
                    $"The ground '{ground.Name}' has no ground under its center ({ground.Area.CenterX}, {ground.Area.CenterZ}).");
            }
        }

        var assignments = PlayingRoster.Assign(scenario, options.BotCount, options.BotStartIndex, options.Seed);
        foreach (var assignment in assignments)
        {
            metrics.AssignRole(assignment.Role, assignment.Kit);
        }

        Console.WriteLine(
            $"Playing bots plan on the collision of World {options.WorldId}, revision {navigation.CollisionWorld.Revision}: "
            + string.Join(", ", PlayingRoster.RoleCounts(scenario, options.BotCount)
                .Where(pair => pair.Value > 0)
                .Select(pair => $"{pair.Value} {PlayingScenario.FormatRole(pair.Key)}")) + ".");

        // The churn takes the real way back through GameService, which only the full stack has.
        var rejoin = scenario.Churn is not null ? admissionProvider as FullStackStressAdmissionProvider : null;
        return new PlayingRunContext(
            scenario,
            assignments.ToDictionary(assignment => assignment.BotIndex),
            PlayingGameData.Load(options.ItemCatalogPath, options.ActorCatalogPath),
            navigation,
            metrics)
        {
            // The way back starts on a pool thread, so nothing of it runs on a poll thread.
            Rejoin = rejoin is null ? null : (plan, token) => Task.Run(() => rejoin.RejoinAsync(plan, token), token),
            ReadRejoinFacts = rejoin is null ? null : (plan, token) => Task.Run(() => rejoin.ReadRejoinFactsAsync(plan, token), token),
            Regeneration = rejoin is null ? new PlayingRegeneration(1, 1) : PlayingRegeneration.Load(options.CombatRulesPath)
        };
    }

    // The churn's events of the steady interval, for the bots that joined.
    private void ScheduleChurn()
    {
        if (playing?.Scenario.Churn is not { } churn || playing.Rejoin is null)
        {
            return;
        }

        var joined = playingBots
            .Where(pair => pair.Value.Bot.State == StressBotState.Joined)
            .Select(pair => pair.Key)
            .ToArray();
        churnSchedule = PlayingChurnSchedule.Create(churn, joined, options.SteadyDuration, options.Seed);
        nextChurnEvent = 0;
        Console.WriteLine(
            $"Churn: {churn.PercentPerMinute} percent of the bots leave and come back every minute, {churnSchedule.Count} leaves in the first {Math.Max(0d, (options.SteadyDuration - PlayingChurnSchedule.QuietTail).TotalSeconds):0} s of the steady interval, half to their standing body and half to their saved place.");
    }

    // Hands every due churn event to its bot's poll thread.
    private void DispatchChurn(long now, long steadyStartedTimestamp)
    {
        while (nextChurnEvent < churnSchedule.Count
            && now >= AddDuration(steadyStartedTimestamp, churnSchedule[nextChurnEvent].Offset))
        {
            var churnEvent = churnSchedule[nextChurnEvent++];
            if (playingBots.TryGetValue(churnEvent.BotIndex, out var entry))
            {
                var bot = entry.Bot;
                entry.Group.Post(timestamp => bot.RequestChurn(churnEvent, timestamp));
            }
        }
    }

    private void LaunchRampStep(CancellationToken cancellationToken)
    {
        var count = Math.Min(options.RampStep, options.BotCount - launchedBots);
        for (var offset = 0; offset < count; offset++)
        {
            var botIndex = options.BotStartIndex + launchedBots;
            pendingAdmissions.Add(new PendingAdmission(
                botIndex,
                admissionProvider.AdmitAsync(botIndex, cancellationToken)));
            launchedBots++;
        }

        Console.WriteLine(
            $"Started {count} admission lifecycles. Total started: {launchedBots}/{options.BotCount}.");
    }

    private async Task CompleteAdmissionsAsync(CancellationToken cancellationToken)
    {
        for (var index = pendingAdmissions.Count - 1; index >= 0; index--)
        {
            var pending = pendingAdmissions[index];
            if (!pending.Task.IsCompleted)
            {
                continue;
            }

            pendingAdmissions.RemoveAt(index);
            StressAdmissionResult result;
            try
            {
                result = await pending.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = StressAdmissionResult.Failure(
                    "stress_admission_failed",
                    exception.Message);
            }

            if (!result.Succeeded)
            {
                admissionFailures.Add(CreateAdmissionFailure(
                    pending.BotIndex,
                    result.ErrorCode ?? "stress_admission_failed",
                    result.ErrorMessage ?? "The stress admission failed."));
                continue;
            }

            var admission = result.Admission!;
            admittedBotIndexes.Add(admission.BotIndex);
            Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refresh =
                token => admissionProvider.RefreshInventoryAsync(admission.BotIndex, token);
            IStressBot bot = playing is null
                ? new StressBot(
                    options,
                    admission,
                    RecordInputAcknowledgement,
                    gameplayMetrics,
                    refresh)
                : new PlayingStressBot(
                    options,
                    admission,
                    playing,
                    RecordInputAcknowledgement,
                    gameplayMetrics,
                    refresh);
            bots.Add(bot);
            // The poll threads share the bots evenly in admission order.
            var group = groups[(bots.Count - 1) % groups.Count];
            group.Add(bot);
            if (bot is PlayingStressBot playingBot)
            {
                playingBots[admission.BotIndex] = (playingBot, group);
            }
        }
    }

    private async Task LeaveAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Steady interval complete. Requesting graceful leave for joined bots.");
        foreach (var group in groups)
        {
            group.ForEachBot((bot, timestamp) => bot.BeginLeave(timestamp));
        }

        var deadline = AddDuration(Stopwatch.GetTimestamp(), options.JoinTimeout + CoordinationInterval);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowOnFailure(Task.CompletedTask);
            if (bots.All(bot => bot.State is StressBotState.Completed or StressBotState.Failed))
            {
                break;
            }

            await Task.Delay(CoordinationInterval, cancellationToken);
        }
    }

    private async Task DrainGameplayAsync(CancellationToken cancellationToken)
    {
        foreach (var group in groups)
        {
            group.ForEachBot((bot, _) => bot.BeginGameplayDrain());
        }

        var drainTimeout = options.GameplayOperationTimeout > options.HttpTimeout
            ? options.GameplayOperationTimeout
            : options.HttpTimeout;
        var deadline = AddDuration(
            Stopwatch.GetTimestamp(),
            drainTimeout + TimeSpan.FromSeconds(1));
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowOnFailure(Task.CompletedTask);
            if (bots.All(bot => bot.IsGameplayIdle))
            {
                return;
            }

            await Task.Delay(CoordinationInterval, cancellationToken);
        }
    }

    // Reads only counters the poll threads write, never a bot's collections.
    private void WriteProgress()
    {
        var progress = bots.Select(bot => bot.CaptureProgress()).ToArray();
        var joined = progress.Count(bot => bot.State == StressBotState.Joined);
        var joining = pendingAdmissions.Count + progress.Count(
            bot => bot.State is StressBotState.Connecting or StressBotState.Joining);
        var failed = admissionFailures.Count
            + progress.Count(bot => bot.State == StressBotState.Failed);
        var receivedSnapshots = progress.Sum(bot => bot.SnapshotsReceived);
        var missingSnapshots = progress.Sum(bot => bot.EstimatedMissingSnapshots);
        StressLatencySummary? acknowledgement;
        lock (timingGate)
        {
            acknowledgement = intervalInputAcknowledgementLatencies.CaptureSummary();
            intervalInputAcknowledgementLatencies.Reset();
        }

        var p95 = acknowledgement is null ? "n/a" : $"{acknowledgement.P95Ms:0.0} ms";
        Console.WriteLine(
            $"Bots joined {joined}, joining {joining}, failed {failed}; snapshots {receivedSnapshots}, estimated missing {missingSnapshots}; input ack p95 {p95}.");
        if (latestDrift is { } drift)
        {
            Console.WriteLine(FormatDrift(drift, options.DatabasePoolSize, options.WorkerHeartbeatTimeoutSeconds));
        }
    }

    public static string FormatDrift(StressDriftSample drift, int? poolSize, int? heartbeatTimeoutSeconds)
    {
        var headroom = drift.MinimumSessionLeaseHeadroomSeconds is { } lease
            ? lease.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
            : "n/a";
        var heartbeat = drift.WorkerHeartbeatAgeSeconds is { } age
            ? age.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s ago"
                + (heartbeatTimeoutSeconds is { } timeout ? $" of {timeout} s" : string.Empty)
            : "n/a";
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Drift: {drift.ActiveSessions} active sessions for {drift.JoinedBots} joined bots, {drift.SessionsNearLeaseExpiry} within {StressPostgresSampler.LeaseExpiryThresholdSeconds:0} s of lease expiry, least lease left {headroom}; worker registry heartbeat {heartbeat}; {drift.PendingItemOperations} pending item operations; GameService connections {drift.ServiceConnectionsInUse} in use of {drift.ServiceConnections}{(poolSize is { } pool ? $", pool {pool}" : string.Empty)}; oldest idle transaction {drift.OldestIdleInTransactionSeconds:0.0} s.");
    }

    private static StressBotSnapshot CreateAdmissionFailure(
        int botIndex,
        string code,
        string message)
    {
        return new StressBotSnapshot(
            botIndex,
            $"Stress Bot {botIndex}",
            StressBotState.Failed,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            code,
            message);
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        return timestamp + (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed record PendingAdmission(
        int BotIndex,
        Task<StressAdmissionResult> Task);
}
