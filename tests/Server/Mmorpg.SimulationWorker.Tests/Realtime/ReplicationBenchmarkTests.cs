using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mmorpg.Testing;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// What one benchmark run measured, as tools/Measure-Replication.ps1 writes it to result.json and
// checks in its baseline. The fingerprint covers the deterministic numbers of every layout.
internal sealed record ReplicationRunResult(
    string? Commit,
    string? SourceState,
    bool? TreeDirty,
    DateTime Date,
    ReplicationMachine Machine,
    string DeterministicFingerprint,
    IReadOnlyList<ReplicationLayoutResult> Layouts);

internal sealed record ReplicationMachine(
    string Cpu,
    int LogicalCores,
    string OperatingSystem,
    string DotNet,
    double? LoadPercentBefore);

public sealed class ReplicationBenchmarkTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DictionaryKeyPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // The three layouts of the replication benchmark. tools/Measure-Replication.ps1 runs it alone,
    // with MMORPG_REPLICATION_RESULT naming the result file; CI and tools/Test-Changes.ps1 leave
    // the category out.
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Benchmark)]
    public void MeasureTheReplicationLayouts()
    {
        var results = new List<ReplicationLayoutResult>();
        foreach (var layout in ReplicationLayout.Measured)
        {
            using var benchmark = new ReplicationBenchmark(layout);
            results.Add(benchmark.Run());
        }

        var run = new ReplicationRunResult(
            Environment.GetEnvironmentVariable("MMORPG_REPLICATION_COMMIT"),
            Environment.GetEnvironmentVariable("MMORPG_REPLICATION_SOURCE_STATE"),
            bool.TryParse(Environment.GetEnvironmentVariable("MMORPG_REPLICATION_TREE_DIRTY"), out var dirty) ? dirty : null,
            DateTime.UtcNow,
            new ReplicationMachine(
                Environment.GetEnvironmentVariable("MMORPG_REPLICATION_CPU") ?? RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                double.TryParse(
                    Environment.GetEnvironmentVariable("MMORPG_REPLICATION_MACHINE_LOAD"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var load)
                    ? load
                    : null),
            Fingerprint(results),
            results);
        var json = JsonSerializer.Serialize(run, SerializerOptions);
        var path = Environment.GetEnvironmentVariable("MMORPG_REPLICATION_RESULT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, json + "\n", new UTF8Encoding(false));
        }

        TestContext.Current.TestOutputHelper?.WriteLine(json);
        Assert.All(results, result => Assert.Equal(0, result.Deterministic.DroppedSnapshotPackets));
    }

    // The small layout pins every deterministic number, so the harness cannot rot unnoticed, and
    // two runs in one process give the same numbers.
    [Fact]
    public void ASmallLayoutKeepsItsDeterministicNumbers()
    {
        ReplicationLayoutResult first;
        ReplicationLayoutResult second;
        using (var benchmark = new ReplicationBenchmark(ReplicationLayout.Small))
        {
            first = benchmark.Run();
        }

        using (var benchmark = new ReplicationBenchmark(ReplicationLayout.Small))
        {
            second = benchmark.Run();
        }

        Assert.Equal(first.Deterministic, second.Deterministic);
        Assert.Equal(40, first.Players);
        Assert.Equal(12, first.Mobs);
        Assert.Equal(24, first.MovingPlayers);
        Assert.Equal(6, first.MovingMobs);
        // After an intended change of replication, take the new numbers from this line.
        var numbers = first.Deterministic;
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(numbers, SerializerOptions));
        Assert.Equal(
            [45L, 797_400L, 3_206L, 800_606L, 3_600L, 41L, 18_000L, 1_800L, 16_200L, 29_717L, 559L, 2_359L, 18L, 23L, 0L],
            [
                numbers.Snapshots, numbers.SnapshotBytes, numbers.ReliableBytes, numbers.AllBytes, numbers.UnreliablePackets,
                numbers.ReliablePackets, numbers.SnapshotRecords, numbers.OwnerRecords, numbers.RemoteRecords,
                numbers.DeferredUpdates, numbers.VisibilityGroups, numbers.PacketsEncoded, numbers.SpawnPackets,
                numbers.DespawnPackets, numbers.DroppedSnapshotPackets
            ]);
        Assert.Equal(6_645d, numbers.SnapshotBytesPerPlayerPerSecond, 9);
        Assert.Equal(6_671.716666666667, numbers.AllBytesPerPlayerPerSecond, 9);
        Assert.Equal(1_213.6666666666667, numbers.PacketsPerSecond, 9);
        Assert.Equal(10d, numbers.RecordsPerPlayerPerSnapshot, 9);
        Assert.Equal(44.3, numbers.BytesPerRecord, 9);
        Assert.Equal(0.6471894940871573, numbers.DeferredShare, 9);
        Assert.Equal(5.390541463782061, numbers.UpdateRateAverageHz, 9);
        Assert.Equal(4.379956741167988, numbers.UpdateRateP5Hz, 9);
        Assert.Equal(4.336188436830835, numbers.UpdateRateMinimumHz, 9);
        Assert.Contains("snapshots", first.Timing.PhaseAverageMs.Keys);
        Assert.Contains("benchmark motion", first.Timing.PhaseAverageMs.Keys);
    }

    internal static string Fingerprint(IReadOnlyList<ReplicationLayoutResult> results)
    {
        var deterministic = JsonSerializer.Serialize(
            results.Select(result => new { result.Name, result.Deterministic }),
            SerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deterministic)));
    }
}
