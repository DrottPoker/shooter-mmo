using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Tests;

// The timing settings that must hold together: the lease that GameService grants must outlast what
// the worker needs between two heartbeats and through an outage that it tolerates, and the host's
// shutdown timeout must cover the steps of the orderly stop. The leases are checked against the first
// heartbeat answer by SimulationWorkerHeartbeatService; the bounds are defined here.
public sealed class SimulationWorkerTimingConfigurationTests
{
    // The leases that the shipped settings of GameService grant, and the shipped settings of the worker,
    // must hold together, or a worker would stop at its first heartbeat.
    [Fact]
    public void TheDefaultsFitTheDefaultLeasesOfGameService()
    {
        var config = Load();
        using var gameService = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Mmorpg.Testing.RepositoryPaths.Root,
            "src",
            "Server",
            "GameService",
            "Config",
            "appsettings.json")));
        var simulation = gameService.RootElement.GetProperty("Simulation");
        var registryLease = TimeSpan.FromSeconds(simulation.GetProperty("WorkerHeartbeatTimeoutSeconds").GetInt32());
        var sessionLease = TimeSpan.FromSeconds(simulation.GetProperty("SessionLeaseLifetimeSeconds").GetInt32());

        Assert.True(config.MinimumRegistryLease < registryLease, config.MinimumRegistryLease.ToString());
        Assert.True(config.MinimumSessionLease <= sessionLease, config.MinimumSessionLease.ToString());
    }

    // Every part of an outage that the worker rides out is in the bound: the tolerance and the poll of
    // the monitor that would stop it just after, the heartbeat that notices the return and its call,
    // and the session heartbeat round that renews the leases, with the retry of one request of it.
    [Fact]
    public void TheMinimumSessionLeaseCountsEveryStepBetweenTheOutageAndTheRenewal()
    {
        var config = Load(
            ("SimulationWorker:GameServiceOutageToleranceSeconds", "100"),
            ("SimulationWorker:RegistryHeartbeatRetryMilliseconds", "3000"),
            ("SimulationWorker:GameServiceTimeoutSeconds", "7"),
            ("SimulationWorker:SimulationSessionHeartbeatSeconds", "20"),
            ("SimulationWorker:SimulationSessionHeartbeatRetryMilliseconds", "4000"),
            ("SimulationWorker:UncertainOperationRetryHorizonSeconds", "600"),
            ("SimulationWorker:ShutdownTimeoutSeconds", "60"));

        Assert.Equal(
            TimeSpan.FromSeconds(100 + 1 + 3 + 7 + 20 + 4),
            config.MinimumSessionLease);
    }

    // The registry lease must outlast a heartbeat interval, the longest that a heartbeat takes to be
    // answered, and the margin that the local lease keeps.
    [Fact]
    public void TheMinimumRegistryLeaseIsTheIntervalPlusTheCallTimeoutPlusTheMargin()
    {
        var config = Load(
            ("SimulationWorker:RegistryHeartbeatSeconds", "12"),
            ("SimulationWorker:GameServiceTimeoutSeconds", "4"),
            ("SimulationWorker:ShutdownTimeoutSeconds", "60"));

        Assert.Equal(TimeSpan.FromSeconds(12 + 4) + SimulationWorkerRegistrationLease.MaximumSafetyMargin, config.MinimumRegistryLease);
    }

    [Fact]
    public void TheShutdownTimeoutHasADefaultThatCoversTheStepsOfTheStop()
    {
        var config = Load();

        Assert.Equal(TimeSpan.FromSeconds(SimulationWorkerConfig.DefaultShutdownTimeoutSeconds), config.ShutdownTimeout);
        Assert.True(
            config.ShutdownTimeout >= SimulationWorkerConfig.MinimumShutdownTimeoutFor(config.GameServiceTimeout),
            "The default shutdown timeout must cover the steps of the stop at the default call timeout.");
        // The host's own default is 30 seconds, which the steps of the stop can outlast.
        Assert.True(
            SimulationWorkerConfig.MinimumShutdownTimeoutFor(config.GameServiceTimeout) > TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("5", "5", "SimulationWorker:ShutdownTimeoutSeconds must be at least 6 times SimulationWorker:GameServiceTimeoutSeconds plus 3 seconds (33 with the current call timeout)")]
    [InlineData("601", "5", "must not exceed 600")]
    [InlineData("40", "8", "(51 with the current call timeout)")]
    public void TheShutdownTimeoutMustCoverTheStepsOfTheStopAtTheCallTimeout(
        string shutdown,
        string callTimeout,
        string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(
            ("SimulationWorker:ShutdownTimeoutSeconds", shutdown),
            ("SimulationWorker:GameServiceTimeoutSeconds", callTimeout)));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains("stop grace period", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostStopsWithinTheConfiguredTimeoutNotItsOwnDefault()
    {
        var config = Load(("SimulationWorker:ShutdownTimeoutSeconds", "75"));
        var services = new ServiceCollection();

        services.AddSimulationWorkerShutdownTimeout(config);

        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(75), host.ShutdownTimeout);
    }

    private static SimulationWorkerConfig Load(params (string Key, string Value)[] overrides)
    {
        var settings = SimulationWorkerConfigurationValidationTests.WorkerSettings();
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return SimulationWorkerConfig.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }
}
