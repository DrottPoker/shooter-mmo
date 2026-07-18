using System.Diagnostics;

namespace ShooterMmo.Tools.SimulationStressGenerator;

public sealed class StressRunCoordinator(
    StressGeneratorOptions options,
    StressAuthorityState authority)
{
    private readonly List<StressBot> bots = [];
    private readonly StressLatencyAccumulator inputAcknowledgementLatencies =
        new(options.Seed);
    private readonly StressLatencyAccumulator intervalInputAcknowledgementLatencies =
        new(unchecked(options.Seed + 1));

    public async Task<StressRunReport> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"Waiting up to {options.WorkerWaitTimeout.TotalSeconds:0} seconds for SimulationWorker '{options.WorkerId}'.");
        var worker = await authority.WaitForWorkerAsync(
            options.WorkerWaitTimeout,
            cancellationToken);
        Console.WriteLine(
            $"Registered worker runtime {worker.RuntimeId} for shard {worker.ShardId} with capacity {worker.MaxConnections}.");
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        using var processSampler = WorkerProcessSampler.TryAttach(worker.StartedAt);
        using var generatorProcessSampler = WorkerProcessSampler.AttachCurrent();
        processSampler?.Sample();
        generatorProcessSampler.Sample();
        if (options.BotCount > worker.MaxConnections)
        {
            Console.WriteLine(
                $"Warning: requested {options.BotCount} bots exceeds the worker capacity {worker.MaxConnections}. Rejections are expected.");
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

                if (bots.Count < options.BotCount && now >= nextRampTimestamp)
                {
                    AddRampStep();
                    nextRampTimestamp = AddDuration(now, options.RampInterval);
                    if (bots.Count == options.BotCount)
                    {
                        rampComplete = true;
                        Console.WriteLine(
                            $"All {options.BotCount} bots started. Waiting for join attempts to finish before steady state.");
                    }
                }

                if (rampComplete
                    && !steadyDeadlineTimestamp.HasValue
                    && bots.All(bot => bot.State is StressBotState.Joined or StressBotState.Failed))
                {
                    processSampler?.MarkSteadyState();
                    generatorProcessSampler.MarkSteadyState();
                    steadyDeadlineTimestamp = AddDuration(now, options.SteadyDuration);
                    var joined = bots.Count(bot => bot.State == StressBotState.Joined);
                    Console.WriteLine(
                        $"Join attempts complete with {joined}/{options.BotCount} bots joined. Beginning {options.SteadyDuration.TotalSeconds:0}-second steady interval.");
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
                    nextProcessSampleTimestamp = AddDuration(now, TimeSpan.FromSeconds(1));
                }

                await Task.Delay(1, cancellationToken);
            }

            WriteProgress();
            processSampler?.Sample();
            generatorProcessSampler.Sample();
            await LeaveAsync(cancellationToken);
            var endedAt = DateTime.UtcNow;
            var snapshots = bots.Select(bot => bot.Capture()).ToArray();
            return StressRunReport.Create(
                startedAt,
                endedAt,
                options,
                authority.CaptureWorkerRegistration() ?? worker,
                processSampler?.CreateSummary(),
                generatorProcessSampler.CreateSummary(),
                snapshots,
                inputAcknowledgementLatencies.CaptureSummary(),
                authority.CaptureCounters());
        }
        finally
        {
            foreach (var bot in bots)
            {
                bot.Dispose();
            }
        }
    }

    private void AddRampStep()
    {
        var count = Math.Min(options.RampStep, options.BotCount - bots.Count);
        for (var offset = 0; offset < count; offset++)
        {
            var botIndex = options.BotStartIndex + bots.Count;
            var ticket = authority.IssueTicket(botIndex);
            var bot = new StressBot(
                options,
                ticket,
                inputAcknowledgementLatencies,
                intervalInputAcknowledgementLatencies);
            bots.Add(bot);
            bot.Start();
        }

        Console.WriteLine($"Started {count} bots. Total started: {bots.Count}/{options.BotCount}.");
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
        var joining = snapshots.Count(
            bot => bot.State is StressBotState.Connecting or StressBotState.Joining);
        var failed = snapshots.Count(bot => bot.State == StressBotState.Failed);
        var receivedSnapshots = snapshots.Sum(bot => bot.SnapshotsReceived);
        var missingSnapshots = snapshots.Sum(bot => bot.EstimatedMissingSnapshots);
        var acknowledgement = intervalInputAcknowledgementLatencies.CaptureSummary();
        intervalInputAcknowledgementLatencies.Reset();
        var p95 = acknowledgement is null ? "n/a" : $"{acknowledgement.P95Ms:0.0} ms";
        Console.WriteLine(
            $"Bots joined {joined}, joining {joining}, failed {failed}; snapshots {receivedSnapshots}, estimated missing {missingSnapshots}; input ack p95 {p95}.");
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        return timestamp + (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }
}
