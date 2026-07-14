using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShooterMmo.Tools.SimulationStressGenerator;

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
    IReadOnlyDictionary<string, int> FailureCodes);

public sealed record StressRunConfiguration(
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
    double RampIntervalSeconds,
    double SteadyDurationSeconds,
    int Seed);

public sealed record StressRunReport(
    DateTime StartedAt,
    DateTime EndedAt,
    double DurationSeconds,
    StressRunConfiguration Configuration,
    StressWorkerRegistration Worker,
    StressProcessSummary? WorkerProcess,
    StressProcessSummary? GeneratorProcess,
    StressBotAggregate Bots,
    StressAuthorityCounters Authority)
{
    public static StressRunReport Create(
        DateTime startedAt,
        DateTime endedAt,
        StressGeneratorOptions options,
        StressWorkerRegistration worker,
        StressProcessSummary? workerProcess,
        StressProcessSummary? generatorProcess,
        IReadOnlyCollection<StressBotSnapshot> bots,
        StressLatencySummary? inputAcknowledgementLatency,
        StressAuthorityCounters authority)
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
            failureCodes);

        return new StressRunReport(
            startedAt,
            endedAt,
            (endedAt - startedAt).TotalSeconds,
            new StressRunConfiguration(
                options.AuthorityUrl.ToString(),
                options.WorkerHost,
                options.WorkerUdpPort,
                options.WorkerId,
                options.FleetId,
                options.NodeId,
                options.ShardId,
                options.WorldId,
                options.BotCount,
                options.BotStartIndex,
                options.RampStep,
                options.RampInterval.TotalSeconds,
                options.SteadyDuration.TotalSeconds,
                options.Seed),
            worker,
            workerProcess,
            generatorProcess,
            aggregate,
            authority);
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
