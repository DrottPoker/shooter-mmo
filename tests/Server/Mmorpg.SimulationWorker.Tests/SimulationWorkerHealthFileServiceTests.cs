using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Health;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Tests.Realtime;

namespace Mmorpg.SimulationWorker.Tests;

// The container health check of the worker reads a file that the worker writes while it is ready:
// its UDP port is bound, its registration lease is valid, and its realtime loop completes ticks.
public sealed class SimulationWorkerHealthFileServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mmorpg-health-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private readonly SteppedTimeProvider time = new(Start);
    private readonly RealtimeTransportReadiness transport = new();
    private readonly RealtimeLoopLiveness liveness = new();
    private readonly CapturingLogger<SimulationWorkerHealthFileService> logger = new();
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
        Tick();

        service.Update();
        transport.MarkListening(27015);
        service.Update();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheFileIsWrittenWhileReadyAndRemovedWhenTheLeaseEnds()
    {
        var service = Create(path);
        BecomeReady();

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
        BecomeReady();
        service.Update();

        // The loop keeps ticking while the lease, which another service renews, runs out.
        for (var second = 0; second < 8; second++)
        {
            time.Advance(TimeSpan.FromSeconds(4));
            Tick();
        }
        service.Update();

        Assert.False(File.Exists(path));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void WithoutAConfiguredPathNothingHappens()
    {
        var service = Create(null);
        BecomeReady();

        service.Update();

        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void ALoopThatKeepsTickingKeepsTheFile()
    {
        var service = Create(path);
        BecomeReady(leaseSeconds: 3600);

        for (var pass = 0; pass < 6; pass++)
        {
            // Ticks every 33 ms for five seconds, the interval between two passes.
            for (var tick = 0; tick < 150; tick++)
            {
                time.Advance(TimeSpan.FromMilliseconds(33.3));
                Tick();
            }

            service.Update();
            Assert.True(File.Exists(path), $"The file is gone at pass {pass}.");
        }

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ALoopThatStopsTickingLosesTheFileAfterTheBoundAndLogsOnceForTheEpisode()
    {
        var service = Create(path);
        BecomeReady(leaseSeconds: 3600);
        service.Update();
        Assert.True(File.Exists(path));

        // Within the bound the worker is still ready: a slow tick is no hang.
        time.Advance(SimulationWorkerHealthFileService.LoopSilenceLimit - TimeSpan.FromMilliseconds(1));
        service.Update();
        Assert.True(File.Exists(path));
        Assert.Empty(logger.Entries);

        // Past it the worker is not ready, and the warning names how long the loop was silent.
        time.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.False(File.Exists(path));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("completed no tick for 6.0 s", warning.Message, StringComparison.Ordinal);

        // A hang that goes on is still one line.
        for (var pass = 0; pass < 5; pass++)
        {
            time.Advance(SimulationWorkerHealthFileService.WriteInterval);
            service.Update();
        }

        Assert.False(File.Exists(path));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void ALoopThatTicksAgainGetsTheFileBackAndAnotherHangIsAnotherEpisode()
    {
        var service = Create(path);
        BecomeReady(leaseSeconds: 3600);
        service.Update();
        time.Advance(TimeSpan.FromSeconds(6));
        service.Update();
        Assert.False(File.Exists(path));
        Assert.Single(logger.Entries);

        Tick();
        service.Update();
        Assert.True(File.Exists(path));

        time.Advance(TimeSpan.FromSeconds(6));
        service.Update();
        Assert.False(File.Exists(path));
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void ALoopThatNeverTickedMakesAWorkerThatWouldBeReadyNotReadyAndSaysSoOnce()
    {
        var service = Create(path);
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(3600));

        service.Update();
        service.Update();

        Assert.False(File.Exists(path));
        var warning = Assert.Single(logger.Entries);
        Assert.Contains("has not completed a tick", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerThatIsNotYetRegisteredIsQuietWhileItsLoopHasNotTicked()
    {
        var service = Create(path);
        transport.MarkListening(27015);

        service.Update();

        Assert.False(File.Exists(path));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void AStoppingWorkerLosesTheFileAndLogsNoHang()
    {
        var lifetime = new StoppingLifetime();
        var service = Create(path, lifetime);
        BecomeReady(leaseSeconds: 3600);
        service.Update();
        Assert.True(File.Exists(path));

        // The loop stops ticking in the orderly stop, which takes seconds: that is no hang.
        lifetime.Stop();
        time.Advance(TimeSpan.FromSeconds(30));
        service.Update();

        Assert.False(File.Exists(path));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task TheServiceRefreshesTheFileEveryIntervalAndDeletesItWhenItStops()
    {
        var service = Create(path);
        BecomeReady();
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitAsync(() => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        // The loop ticks right before the pass that the interval makes due.
        Tick();
        time.Advance(SimulationWorkerHealthFileService.WriteInterval);
        Tick();
        await WaitAsync(() => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileThatCannotBeWrittenNeverStopsTheWorker()
    {
        var blocked = Path.Combine(directory, "missing-folder", "worker-ready");
        var service = Create(blocked);
        BecomeReady();

        service.Update();
        service.Update();

        Assert.False(File.Exists(blocked));
    }

    [Fact]
    public void TheLivenessCountsTheTimeSinceTheLastTickOnTheClockThatReadsIt()
    {
        Assert.False(liveness.HasTicked);
        Assert.Null(liveness.SilenceFor(time));

        Tick();
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.True(liveness.HasTicked);
        Assert.Equal(TimeSpan.FromMilliseconds(250), liveness.SilenceFor(time));
    }

    private void BecomeReady(int leaseSeconds = 30)
    {
        transport.MarkListening(27015);
        lease.Renew(lease.CaptureRenewalStart(), TimeSpan.FromSeconds(leaseSeconds));
        Tick();
    }

    // The realtime loop completing a tick now.
    private void Tick()
    {
        liveness.TickCompleted(time.GetTimestamp());
    }

    private SimulationWorkerHealthFileService Create(string? healthFilePath, IHostApplicationLifetime? lifetime = null)
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
            liveness,
            logger,
            time,
            lifetime);
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

    private sealed class StoppingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Stop()
        {
            stopping.Cancel();
        }

        public void StopApplication()
        {
            Stop();
        }
    }
}
