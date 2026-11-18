using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Health;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Tests;

// The container health check of the worker reads a file that the worker writes while it is ready:
// its UDP port is bound and its registration lease is valid.
public sealed class SimulationWorkerHealthFileServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mmorpg-health-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private readonly SteppedTimeProvider time = new(Start);
    private readonly RealtimeTransportReadiness transport = new();
    private readonly SimulationWorkerRegistrationLease lease;

    public SimulationWorkerHealthFileServiceTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "worker-ready");
        lease = new SimulationWorkerRegistrationLease(time);
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void NothingIsWrittenBeforeTheWorkerListensAndHoldsALease()
    {
        var service = Create(path);

        service.Update();
        transport.MarkListening(27015);
        service.Update();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheFileIsWrittenWhileReadyAndRemovedWhenTheLeaseEnds()
    {
        var service = Create(path);
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(30));

        service.Update();
        Assert.True(File.Exists(path));

        lease.Clear();
        service.Update();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheFileIsRemovedWhenTheLeaseRunsOut()
    {
        var service = Create(path);
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(30));
        service.Update();

        time.Advance(TimeSpan.FromSeconds(31));
        service.Update();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WithoutAConfiguredPathNothingHappens()
    {
        var service = Create(null);
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(30));

        service.Update();

        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task TheServiceRefreshesTheFileEveryIntervalAndDeletesItWhenItStops()
    {
        var service = Create(path);
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromMinutes(5));
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitAsync(() => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        time.Advance(SimulationWorkerHealthFileService.WriteInterval);
        await WaitAsync(() => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileThatCannotBeWrittenNeverStopsTheWorker()
    {
        var blocked = Path.Combine(directory, "missing-folder", "worker-ready");
        var service = Create(blocked);
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(30));

        service.Update();
        service.Update();

        Assert.False(File.Exists(blocked));
    }

    private SimulationWorkerHealthFileService Create(string? healthFilePath)
    {
        var settings = SimulationWorkerConfigurationValidationTests.WorkerSettings();
        if (healthFilePath is not null)
        {
            settings["SimulationWorker:HealthFilePath"] = healthFilePath;
        }

        var config = SimulationWorkerConfig.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return new SimulationWorkerHealthFileService(
            config,
            transport,
            lease,
            NullLogger<SimulationWorkerHealthFileService>.Instance,
            time);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The health file was not refreshed.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
