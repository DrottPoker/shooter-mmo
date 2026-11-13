using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmorpg.Tools.StackStressGenerator;

public sealed record StressLatencySummary(
    long Samples,
    double MinimumMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaximumMs,
    double AverageMs)
{
    public static StressLatencySummary? Create(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        return new StressLatencySummary(
            values.Length,
            values[0],
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values[^1],
            values.Average());
    }

    internal static StressLatencySummary CreateFromSample(
        long samples,
        double minimumMs,
        double maximumMs,
        double averageMs,
        IReadOnlyList<double> sortedSample)
    {
        return new StressLatencySummary(
            samples,
            minimumMs,
            Percentile(sortedSample, 0.50),
            Percentile(sortedSample, 0.95),
            Percentile(sortedSample, 0.99),
            maximumMs,
            averageMs);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}

public sealed record StressBotAggregate(
    int RequestedBots,
    int JoinedBots,
    int CompletedBots,
    int FailedBots,
    int OtherBots,
    long PacketsSent,
    long BytesSent,
    long PacketsReceived,
    long BytesReceived,
    long SnapshotsReceived,
    long EstimatedMissingSnapshots,
    long SpawnPackets,
    long DespawnPackets,
    long UnacknowledgedInputsDropped,
    uint LatestServerTick,
    StressLatencySummary? JoinLatency,
    StressLatencySummary? InputAcknowledgementLatency,
    IReadOnlyDictionary<string, int> FailureCodes,
    StressRemoteCadenceSummary? RemoteCadence = null);

// How often the bots received the state of the remote players they saw during the steady
// interval. Each bot's rate is its received states per second over every remote player it saw,
// weighted by how long it saw each; the average, p5, and minimum are over bots. PairP5Hz is the
// fifth percentile over every bot and remote player pair.
public sealed record StressRemoteCadenceSummary(
    int Bots,
    int Pairs,
    double AverageHz,
    double P5Hz,
    double MinimumHz,
    double PairP5Hz,
    long UntrackedRecords)
{
    public static StressRemoteCadenceSummary? Create(
        IEnumerable<Mmorpg.Tools.SimulationBotClient.SimulationBotRemoteCadenceSummary?> bots)
    {
        var measured = bots
            .OfType<Mmorpg.Tools.SimulationBotClient.SimulationBotRemoteCadenceSummary>()
            .Where(bot => bot.UpdatesPerSecond is not null)
            .ToArray();
        if (measured.Length == 0)
        {
            return null;
        }

        var rates = measured.Select(bot => bot.UpdatesPerSecond!.Value).Order().ToArray();
        var pairs = measured
            .SelectMany(bot => bot.PerEntity)
            .Where(pair => pair.VisibleSeconds > 0)
            .Select(pair => pair.Updates / pair.VisibleSeconds)
            .Order()
            .ToArray();
        return new StressRemoteCadenceSummary(
            measured.Length,
            pairs.Length,
            rates.Average(),
            Percentile(rates, 0.05),
            rates[0],
            pairs.Length == 0 ? 0 : Percentile(pairs, 0.05),
            measured.Sum(bot => bot.UntrackedRecords));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}

// The generator's own timing in the steady interval: how late the latest bot's poll ran against
// the poll interval in every poll round, and how long route plans waited for a planner thread.
public sealed record StressGeneratorTimingSummary(
    double PollIntervalMs,
    StressLatencySummary? PollLateness,
    StressLatencySummary? RouteQueue,
    bool PlansRoutes);

public sealed record StressRunConfiguration(
    string Mode,
    string Workload,
    string AuthorityUrl,
    string WorkerHost,
    int WorkerUdpPort,
    string WorkerId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    int BotCount,
    int BotStartIndex,
    int RampStep,
    int SessionValidationRequestsPerBot,
    double RampIntervalSeconds,
    double SteadyDurationSeconds,
    double InventoryOperationIntervalSeconds,
    double LootOperationIntervalSeconds,
    int Seed);

public sealed record StressRunReport(
    DateTime StartedAt,
    DateTime EndedAt,
    double DurationSeconds,
    StressRunConfiguration Configuration,
    StressWorkerRegistration? Worker,
    StressProcessSummary? WorkerProcess,
    StressProcessSummary? GameServiceProcess,
    StressProcessSummary? GeneratorProcess,
    StressPostgresSummary? Postgres,
    StressFullStackSummary? FullStack,
    StressGameplaySummary Gameplay,
    StressBotAggregate Bots,
    StressAuthorityCounters? Authority)
{
    // The steady interval, in which the worker's steady intervals and the remote cadence count.
    public DateTime? SteadyStartedAt { get; init; }

    public DateTime? SteadyEndedAt { get; init; }

    // The worker's own performance file folded over the steady interval, when it was given.
    public StressWorkerPerformanceSummary? WorkerPerformance { get; init; }

    // The judgement against the quality boundary, when one was given.
    public StressVerdict? Verdict { get; init; }

    // The playing workload's roles, counters, timings, and expected conflicts.
    public PlayingSummary? Playing { get; init; }

    // How far the generator itself kept up, which the validity rules judge.
    public StressGeneratorTimingSummary? GeneratorTiming { get; init; }

    public static StressRunReport Create(
        DateTime startedAt,
        DateTime endedAt,
        StressGeneratorOptions options,
        StressRunTarget target,
        StressProviderReport providerReport,
        StressProcessSummary? workerProcess,
        StressProcessSummary? generatorProcess,
        IReadOnlyCollection<StressBotSnapshot> bots,
        StressLatencySummary? inputAcknowledgementLatency,
        StressGameplaySummary gameplay)
    {
        var failureCodes = bots
            .Where(bot => bot.FailureCode is not null)
            .GroupBy(bot => bot.FailureCode!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var joinedBots = bots.Count(bot => bot.JoinLatencyMs is not null);
        var completedBots = bots.Count(bot => bot.State == StressBotState.Completed);
        var failedBots = bots.Count(bot => bot.State == StressBotState.Failed);
        var aggregate = new StressBotAggregate(
            options.BotCount,
            joinedBots,
            completedBots,
            failedBots,
            bots.Count - completedBots - failedBots,
            bots.Sum(bot => bot.PacketsSent),
            bots.Sum(bot => bot.BytesSent),
            bots.Sum(bot => bot.PacketsReceived),
            bots.Sum(bot => bot.BytesReceived),
            bots.Sum(bot => bot.SnapshotsReceived),
            bots.Sum(bot => bot.EstimatedMissingSnapshots),
            bots.Sum(bot => bot.SpawnPackets),
            bots.Sum(bot => bot.DespawnPackets),
            bots.Sum(bot => bot.UnacknowledgedInputsDropped),
            bots.Count == 0 ? 0 : bots.Max(bot => bot.LatestServerTick),
            StressLatencySummary.Create(
                bots.Where(bot => bot.JoinLatencyMs is not null)
                    .Select(bot => bot.JoinLatencyMs!.Value)),
            inputAcknowledgementLatency,
            failureCodes,
            StressRemoteCadenceSummary.Create(bots.Select(bot => bot.RemoteCadence)));

        return new StressRunReport(
            startedAt,
            endedAt,
            (endedAt - startedAt).TotalSeconds,
            new StressRunConfiguration(
                StressGeneratorOptions.FormatMode(options.Mode),
                StressGeneratorOptions.FormatWorkload(options.Workload),
                options.AuthorityUrl.ToString(),
                providerReport.Worker?.Host ?? options.WorkerHost,
                providerReport.Worker?.UdpPort ?? options.WorkerUdpPort,
                options.WorkerId,
                options.FleetId,
                options.NodeId,
                target.ShardId,
                target.WorldId,
                options.BotCount,
                options.BotStartIndex,
                options.RampStep,
                options.SessionValidationRequestsPerBot,
                options.RampInterval.TotalSeconds,
                options.SteadyDuration.TotalSeconds,
                options.InventoryOperationInterval.TotalSeconds,
                options.LootOperationInterval.TotalSeconds,
                options.Seed),
            providerReport.Worker ?? target.Worker,
            workerProcess,
            providerReport.GameServiceProcess,
            generatorProcess,
            providerReport.Postgres,
            providerReport.FullStack,
            gameplay,
            aggregate,
            providerReport.Authority);
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
