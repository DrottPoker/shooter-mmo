using System.Diagnostics;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed class StressRunCoordinator(
    StressGeneratorOptions options,
    IStressAdmissionProvider admissionProvider)
{
    private readonly List<StressBot> bots = [];
    private readonly List<StressBotSnapshot> admissionFailures = [];
    private readonly List<PendingAdmission> pendingAdmissions = [];
    private readonly HashSet<int> admittedBotIndexes = [];
    private readonly StressLatencyAccumulator inputAcknowledgementLatencies =
        new(options.Seed);
    private readonly StressLatencyAccumulator intervalInputAcknowledgementLatencies =
        new(unchecked(options.Seed + 1));
    private int launchedBots;

    public async Task<StressRunReport> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"Waiting up to {options.WorkerWaitTimeout.TotalSeconds:0} seconds for {StressGeneratorOptions.FormatMode(options.Mode)} readiness.");
        var target = await admissionProvider.WaitForReadyAsync(
            options.WorkerWaitTimeout,
            cancellationToken);
        Console.WriteLine(
            $"Stress target shard {target.ShardId}, World {target.WorldId}, capacity {target.Capacity} is ready.");
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        using var processSampler = target.Worker is not null
            ? StressProcessSampler.TryAttach(target.Worker.StartedAt)
            : StressProcessSampler.TryAttach("SimulationWorker");
        using var generatorProcessSampler = StressProcessSampler.AttachCurrent();
        processSampler?.Sample();
        generatorProcessSampler.Sample();
        if (options.BotCount > target.Capacity)
        {
            Console.WriteLine(
                $"Warning: requested {options.BotCount} bots exceeds the target capacity {target.Capacity}. Rejections are expected.");
        }

        var startedAt = DateTime.UtcNow;
        var startedTimestamp = Stopwatch.GetTimestamp();
        var nextRampTimestamp = startedTimestamp;
        var nextReportTimestamp = AddDuration(startedTimestamp, options.ReportInterval);
        var nextProcessSampleTimestamp = AddDuration(startedTimestamp, TimeSpan.FromSeconds(1));
        long? steadyDeadlineTimestamp = null;
        var rampComplete = false;

        try
        {
            while (!steadyDeadlineTimestamp.HasValue
                || Stopwatch.GetTimestamp() < steadyDeadlineTimestamp.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = Stopwatch.GetTimestamp();
                PollBots(now);
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
                    processSampler?.MarkSteadyState();
                    generatorProcessSampler.MarkSteadyState();
                    admissionProvider.MarkSteadyState();
                    var joined = bots.Count(bot => bot.State == StressBotState.Joined);
                    steadyDeadlineTimestamp = joined == 0
                        ? now
                        : AddDuration(now, options.SteadyDuration);
                    Console.WriteLine(
                        $"Admission complete with {joined}/{options.BotCount} bots joined and {admissionFailures.Count} HTTP admission failures. Beginning {(joined == 0 ? 0 : options.SteadyDuration.TotalSeconds):0}-second steady interval.");
                }

                if (now >= nextReportTimestamp)
                {
                    WriteProgress();
                    nextReportTimestamp = AddDuration(now, options.ReportInterval);
                }

                if (now >= nextProcessSampleTimestamp)
                {
                    processSampler?.Sample();
                    generatorProcessSampler.Sample();
                    await admissionProvider.SampleAsync(cancellationToken);
                    nextProcessSampleTimestamp = AddDuration(now, TimeSpan.FromSeconds(1));
                }

                await Task.Delay(1, cancellationToken);
            }

            WriteProgress();
            processSampler?.Sample();
            generatorProcessSampler.Sample();
            await admissionProvider.SampleAsync(cancellationToken);
            await LeaveAsync(cancellationToken);
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
            return StressRunReport.Create(
                startedAt,
                endedAt,
                options,
                target,
                providerReport,
                processSampler?.CreateSummary(),
                generatorProcessSampler.CreateSummary(),
                snapshots,
                inputAcknowledgementLatencies.CaptureSummary());
        }
        finally
        {
            foreach (var bot in bots)
            {
                bot.Dispose();
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
            var bot = new StressBot(
                options,
                admission,
                inputAcknowledgementLatencies,
                intervalInputAcknowledgementLatencies);
            bots.Add(bot);
            bot.Start();
        }
    }

    private void PollBots(long nowTimestamp)
    {
        foreach (var bot in bots)
        {
            bot.Poll(nowTimestamp);
        }
    }

    private async Task LeaveAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Steady interval complete. Requesting graceful leave for joined bots.");
        var now = Stopwatch.GetTimestamp();
        foreach (var bot in bots)
        {
            bot.BeginLeave(now);
        }

        var deadline = AddDuration(now, options.JoinTimeout);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            now = Stopwatch.GetTimestamp();
            PollBots(now);
            if (bots.All(bot => bot.State is StressBotState.Completed or StressBotState.Failed))
            {
                break;
            }

            await Task.Delay(1, cancellationToken);
        }

        PollBots(Stopwatch.GetTimestamp());
    }

    private void WriteProgress()
    {
        var snapshots = bots.Select(bot => bot.Capture()).ToArray();
        var joined = snapshots.Count(bot => bot.State == StressBotState.Joined);
        var joining = pendingAdmissions.Count + snapshots.Count(
            bot => bot.State is StressBotState.Connecting or StressBotState.Joining);
        var failed = admissionFailures.Count
            + snapshots.Count(bot => bot.State == StressBotState.Failed);
        var receivedSnapshots = snapshots.Sum(bot => bot.SnapshotsReceived);
        var missingSnapshots = snapshots.Sum(bot => bot.EstimatedMissingSnapshots);
        var acknowledgement = intervalInputAcknowledgementLatencies.CaptureSummary();
        intervalInputAcknowledgementLatencies.Reset();
        var p95 = acknowledgement is null ? "n/a" : $"{acknowledgement.P95Ms:0.0} ms";
        Console.WriteLine(
            $"Bots joined {joined}, joining {joining}, failed {failed}; snapshots {receivedSnapshots}, estimated missing {missingSnapshots}; input ack p95 {p95}.");
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
