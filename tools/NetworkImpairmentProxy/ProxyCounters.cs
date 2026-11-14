using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmorpg.Tools.NetworkImpairmentProxy;

public enum ProxyDirection
{
    ClientToServer,
    ServerToClient
}

// The counts of one direction. The receive loops of several clients add to the receive side
// at once, so it counts with Interlocked; only the direction's scheduler thread writes the send
// side and the histograms.
public sealed class DirectionCounters
{
    private long packetsReceived;
    private long bytesReceived;
    private long packetsDuplicated;
    private long droppedRandom;
    private long droppedBurst;
    private long droppedOutage;
    private long droppedOverflow;
    private long packetsForwarded;
    private long bytesForwarded;
    private long sendFailures;
    private long discardedAtStop;

    // The time from a packet's arrival to its send, and how much later than its due time it left.
    public MillisecondHistogram Delay { get; } = new(0.1, 10_000);

    public MillisecondHistogram Lateness { get; } = new(0.01, 1_000);

    public void RecordReceived(int bytes)
    {
        Interlocked.Increment(ref packetsReceived);
        Interlocked.Add(ref bytesReceived, bytes);
    }

    public void RecordDropped(PacketFate fate)
    {
        switch (fate)
        {
            case PacketFate.DroppedRandom:
                Interlocked.Increment(ref droppedRandom);
                break;
            case PacketFate.DroppedBurst:
                Interlocked.Increment(ref droppedBurst);
                break;
            case PacketFate.DroppedOutage:
                Interlocked.Increment(ref droppedOutage);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fate), fate, "A forwarded packet is not dropped.");
        }
    }

    public void RecordDuplicated()
    {
        Interlocked.Increment(ref packetsDuplicated);
    }

    public void RecordOverflow()
    {
        Interlocked.Increment(ref droppedOverflow);
    }

    public void RecordSent(int bytes, double delayMs, double latenessMs)
    {
        packetsForwarded++;
        bytesForwarded += bytes;
        Delay.Record(delayMs);
        Lateness.Record(latenessMs);
    }

    public void RecordSendFailure()
    {
        Interlocked.Increment(ref sendFailures);
    }

    public void RecordDiscardedAtStop(int packets)
    {
        Interlocked.Add(ref discardedAtStop, packets);
    }

    public ProxyDirectionCounts Capture()
    {
        return new ProxyDirectionCounts(
            Interlocked.Read(ref packetsReceived),
            Interlocked.Read(ref bytesReceived),
            Volatile.Read(ref packetsForwarded),
            Volatile.Read(ref bytesForwarded),
            Interlocked.Read(ref packetsDuplicated),
            Interlocked.Read(ref droppedRandom),
            Interlocked.Read(ref droppedBurst),
            Interlocked.Read(ref droppedOutage),
            Interlocked.Read(ref droppedOverflow),
            Interlocked.Read(ref sendFailures),
            Interlocked.Read(ref discardedAtStop),
            Delay.Summarize(),
            Lateness.Summarize());
    }
}

// A fixed-resolution histogram of milliseconds for one writer. Percentiles are the upper bound of
// their bucket; a value above the range counts in the last bucket, and the maximum stays exact.
public sealed class MillisecondHistogram
{
    private readonly double resolutionMs;
    private readonly long[] buckets;
    private long samples;
    private double sum;
    private double minimum = double.MaxValue;
    private double maximum;

    public MillisecondHistogram(double resolutionMs, double rangeMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolutionMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(rangeMs, resolutionMs);
        this.resolutionMs = resolutionMs;
        buckets = new long[(int)Math.Ceiling(rangeMs / resolutionMs) + 1];
    }

    public long Samples => Volatile.Read(ref samples);

    public void Record(double milliseconds)
    {
        var value = Math.Max(0d, milliseconds);
        var index = (int)Math.Min(buckets.Length - 1, Math.Ceiling(value / resolutionMs));
        buckets[index]++;
        sum += value;
        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);
        Volatile.Write(ref samples, samples + 1);
    }

    public double Percentile(double fraction)
    {
        var count = Samples;
        if (count == 0)
        {
            return 0;
        }

        var rank = (long)Math.Ceiling(fraction * count);
        var seen = 0L;
        for (var index = 0; index < buckets.Length; index++)
        {
            seen += buckets[index];
            if (seen >= rank)
            {
                return index == buckets.Length - 1 ? maximum : Math.Min(maximum, index * resolutionMs);
            }
        }

        return maximum;
    }

    public ProxyMillisecondSummary? Summarize()
    {
        var count = Samples;
        return count == 0
            ? null
            : new ProxyMillisecondSummary(
                count,
                Math.Round(minimum, 3),
                Math.Round(Percentile(0.50), 3),
                Math.Round(Percentile(0.95), 3),
                Math.Round(Percentile(0.99), 3),
                Math.Round(maximum, 3),
                Math.Round(sum / count, 3));
    }
}

public sealed record ProxyMillisecondSummary(
    long Samples,
    double Minimum,
    double P50,
    double P95,
    double P99,
    double Maximum,
    double Average);

public sealed record ProxyDirectionCounts(
    long PacketsReceived,
    long BytesReceived,
    long PacketsForwarded,
    long BytesForwarded,
    long PacketsDuplicated,
    long DroppedRandom,
    long DroppedBurst,
    long DroppedOutage,
    long DroppedOverflow,
    long SendFailures,
    long DiscardedAtStop,
    ProxyMillisecondSummary? DelayMs,
    ProxyMillisecondSummary? LatenessMs)
{
    // Every packet the rules dropped, without the overflow of a proxy that fell behind.
    [JsonIgnore]
    public long DroppedByRule => DroppedRandom + DroppedBurst + DroppedOutage;

    // The share of the received packets that the rules dropped, in percent.
    [JsonIgnore]
    public double DroppedPercent => PacketsReceived == 0 ? 0 : DroppedByRule * 100d / PacketsReceived;
}

// What the proxy did, which it prints when it stops and writes as JSON on request.
public sealed record ProxyCountsReport(
    string Profile,
    long Seed,
    DateTime StartedAt,
    DateTime EndedAt,
    double DurationSeconds,
    string Listen,
    string Forward,
    int ClientsSeen,
    int ClientsTimedOut,
    int ClientsRefused,
    int MaximumActiveClients,
    ProxyDirectionCounts ClientToServer,
    ProxyDirectionCounts ServerToClient)
{
    // The processor seconds the proxy's process used, and their share of one core over the run.
    public double ProcessorSeconds { get; init; }

    public double AverageSingleCoreCpuPercent => DurationSeconds <= 0 ? 0 : Math.Round(ProcessorSeconds * 100d / DurationSeconds, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public static ProxyCountsReport Parse(string json)
    {
        return JsonSerializer.Deserialize<ProxyCountsReport>(json, SerializerOptions)
            ?? throw new InvalidDataException("The proxy counts are empty.");
    }

    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(FormattableString.Invariant(
            $"Network impairment proxy with profile {Profile} (seed {Seed}) ran {DurationSeconds:0.0} s: {ClientsSeen} clients seen, at most {MaximumActiveClients} at once, {ClientsTimedOut} timed out, {ClientsRefused} refused; it used {ProcessorSeconds:0.0} processor seconds, {AverageSingleCoreCpuPercent:0.0} % of one core."));
        WriteDirection(writer, "client to server", ClientToServer);
        WriteDirection(writer, "server to client", ServerToClient);
    }

    private static void WriteDirection(TextWriter writer, string name, ProxyDirectionCounts counts)
    {
        writer.WriteLine(FormattableString.Invariant(
            $"  {name}: {counts.PacketsReceived} packets in ({counts.BytesReceived} bytes), {counts.PacketsForwarded} forwarded ({counts.BytesForwarded} bytes), {counts.PacketsDuplicated} duplicated; dropped {counts.DroppedRandom} at random, {counts.DroppedBurst} in bursts, {counts.DroppedOutage} in outages ({counts.DroppedPercent:0.00} %), {counts.DroppedOverflow} by overflow; {counts.SendFailures} send failures, {counts.DiscardedAtStop} discarded at stop."));
        if (counts.DelayMs is { } delay)
        {
            writer.WriteLine(FormattableString.Invariant(
                $"    applied delay ms: min {delay.Minimum:0.00}, p50 {delay.P50:0.00}, p95 {delay.P95:0.00}, p99 {delay.P99:0.00}, max {delay.Maximum:0.00}, average {delay.Average:0.00}"));
        }

        if (counts.LatenessMs is { } lateness)
        {
            writer.WriteLine(FormattableString.Invariant(
                $"    lateness against the due time ms: p50 {lateness.P50:0.000}, p95 {lateness.P95:0.000}, p99 {lateness.P99:0.000}, max {lateness.Maximum:0.000}"));
        }
    }
}

// Stopwatch ticks and milliseconds.
public static class ProxyTime
{
    public static double Milliseconds(long ticks)
    {
        return ticks * 1000d / Stopwatch.Frequency;
    }

    public static long Ticks(double milliseconds)
    {
        return (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d);
    }
}
