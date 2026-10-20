using Microsoft.Extensions.Configuration;
using Mmorpg.Server.Common.Items;
using Mmorpg.Server.Common.Worlds;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class SimulationWorkerConfigurationValidationTests
{
    [Fact]
    public void SimulationWorkerRejectsInvalidPortsAndMissingSecret()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:UdpPort"] = "70000";
        settings["SimulationWorker:AdvertisedHost"] = "http://world.example.test/path";
        settings["SimulationWorker:AdvertisedUdpPort"] = "70001";
        settings.Remove("SimulationWorker:GameServiceSecret");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("UdpPort must be between 1 and 65535", exception.Message);
        Assert.Contains("AdvertisedUdpPort must be between 1 and 65535", exception.Message);
        Assert.Contains("AdvertisedHost must be a host name or IP address", exception.Message);
        Assert.Contains("GameServiceSecret is required", exception.Message);
    }

    [Fact]
    public void SimulationWorkerAcceptsCompleteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(WorkerSettings())
            .Build();

        var config = SimulationWorkerConfig.FromConfiguration(configuration);

        Assert.Equal("local-simulation-worker-1", config.SimulationWorkerId);
        Assert.Equal("local-fleet", config.FleetId);
        Assert.Equal("local-node-1", config.NodeId);
        Assert.Equal("local-shard-1", config.ShardId);
        Assert.Equal("development-world-1", config.WorldId);
        Assert.Equal(
            WorldManifestFileStore.ResolveActorRuntimePath(
                WorldDataRootPath(),
                "development-world-1"),
            config.ActorDataPath);
        Assert.Equal("127.0.0.1", config.AdvertisedHost);
        Assert.Equal(27015, config.AdvertisedUdpPort);
        Assert.Equal(100, config.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), config.RegistryHeartbeatInterval);
        Assert.Equal(8, config.SimulationSessionHeartbeatMaxConcurrency);
        Assert.Equal(128f, config.InterestManagement.EnterRadius);
        Assert.Equal(3, config.InterestManagement.RefreshIntervalSnapshots);
        Assert.Equal(
            9_500,
            config.SnapshotReplication.OverloadTargetUtilizationBasisPoints);
        Assert.Equal(2, config.CollisionStreaming.LoadRadiusChunks);
        Assert.Equal(38 * 1024 * 1024, config.UdpQuotas.AggregateSnapshotBytesPerSecond);
        Assert.Equal(4 * 1024 * 1024, config.UdpQuotas.AggregateSnapshotByteBurst);
        Assert.Equal(30, config.MovementSimulation.TickRateHz);
        Assert.Equal(15, config.SnapshotRateHz);
        Assert.Equal(0.35f, config.MovementSimulation.CharacterCollision.Radius);
        Assert.Equal(3f, config.ItemInteraction.CorpseInteractionRadius);
        Assert.Equal(32f, config.ItemInteraction.CorpseDiscoveryRadius);
        Assert.Equal(TimeSpan.FromSeconds(30), config.ItemInteraction.DurableCorpseRefreshInterval);
    }

    [Fact]
    public void SimulationWorkerReadsTheItemCatalogCopiedToItsOutputUnlessConfigured()
    {
        var defaults = SimulationWorkerConfig.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(WorkerSettings())
            .Build());
        var settings = WorkerSettings();
        settings["SimulationWorker:ItemCatalogPath"] = RepositoryPaths.ItemCatalogFixtureRuntimePath;
        var configured = SimulationWorkerConfig.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build());

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                SimulationWorkerConfig.DefaultItemCatalogPath)),
            defaults.ItemCatalogPath);
        Assert.Equal(
            ItemCatalogTestData.Compile().Revision,
            ItemCatalogFileLoader.Load(defaults.ItemCatalogPath).Revision);
        Assert.Equal(
            Path.GetFullPath(RepositoryPaths.ItemCatalogFixtureRuntimePath),
            configured.ItemCatalogPath);
    }

    [Fact]
    public void SimulationWorkerDerivesActorDataPathFromWorldIdentity()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:WorldId"] = "development-world-2";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var config = SimulationWorkerConfig.FromConfiguration(configuration);
        var manifest = WorldManifestFileStore.Load(WorldDataRootPath(), "development-world-2");

        Assert.Equal(
            WorldManifestFileStore.ResolveActorRuntimePath(
                WorldDataRootPath(),
                "development-world-2"),
            config.ActorDataPath);
        Assert.Equal(manifest.bounds.minimumX, config.MovementSimulation.MinimumX);
        Assert.Equal(manifest.bounds.maximumX, config.MovementSimulation.MaximumX);
        Assert.Equal(manifest.spawn.z, config.MovementSpawn.Z);
        Assert.NotEqual(
            WorldManifestFileStore.Load(WorldDataRootPath(), "development-world-1").bounds.maximumX,
            config.MovementSimulation.MaximumX);
    }

    [Fact]
    public void SimulationWorkerRejectsMissingSelectedWorldManifest()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:WorldId"] = "missing-world";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains(
            "could not load the configured World",
            exception.Message);
    }

    [Fact]
    public void DevelopmentGlobalItemAccessIsExplicitAndEnvironmentGuarded()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentItemInteractions:GlobalBankAndRecoveryAccess"] = "true"
            })
            .Build();

        var productionError = Assert.Throws<InvalidOperationException>(() =>
            DevelopmentItemInteractionOptions.FromConfiguration(
                configuration,
                isDevelopment: false));
        Assert.Contains("can only be true in the Development environment", productionError.Message);

        var options = DevelopmentItemInteractionOptions.FromConfiguration(
            configuration,
            isDevelopment: true);
        Assert.True(options.GlobalBankAndRecoveryAccess);
    }

    [Fact]
    public void SimulationWorkerRejectsUnsafeResilienceLimits()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:SimulationSessionHeartbeatMaxConcurrency"] = "129";
        settings["SimulationWorker:UdpQuotas:InboundPacketBurst"] = "20001";
        settings["SimulationWorker:UdpQuotas:AggregateSnapshotBytesPerSecond"] = "268435457";
        settings["SimulationWorker:InterestManagement:CellSize"] = "1";
        settings["SimulationWorker:InterestManagement:ExitRadius"] = "100";
        settings["SimulationWorker:CollisionStreaming:UnloadRadiusChunks"] = "17";
        settings["SimulationWorker:RegistryAcquireTimeoutSeconds"] = "10";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("SimulationSessionHeartbeatMaxConcurrency must not exceed 128", exception.Message);
        Assert.Contains("UDP quota rates or bursts exceed", exception.Message);
        Assert.Contains("aggregate snapshot quota exceeds", exception.Message);
        Assert.Contains("no more than 64 searched cells", exception.Message);
        Assert.Contains("UnloadRadiusChunks must not exceed 16", exception.Message);
        Assert.Contains(
            "RegistryAcquireTimeoutSeconds must be longer than RegistryHeartbeatSeconds",
            exception.Message);
    }

    [Fact]
    public void SimulationWorkerRejectsAnInterestRefreshIntervalLongerThanOneSecond()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:InterestManagement:RefreshIntervalSnapshots"] = "16";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("RefreshIntervalSnapshots must not exceed one second", exception.Message);
    }

    [Fact]
    public void SimulationWorkerRejectsInvalidSharedItemInteractionRadii()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:ItemInteraction:CorpseInteractionRadius"] = "21";
        settings["SimulationWorker:ItemInteraction:CorpseDiscoveryRadius"] = "2";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("CorpseInteractionRadius must be greater than 0 and at most 20", exception.Message);
        Assert.Contains("CorpseDiscoveryRadius must be at least the interaction radius", exception.Message);
    }

    private static Dictionary<string, string?> WorkerSettings()
    {
        return new Dictionary<string, string?>
        {
            ["SimulationWorker:SimulationWorkerId"] = "local-simulation-worker-1",
            ["SimulationWorker:FleetId"] = "local-fleet",
            ["SimulationWorker:NodeId"] = "local-node-1",
            ["SimulationWorker:ShardId"] = "local-shard-1",
            ["SimulationWorker:WorldId"] = "development-world-1",
            ["SimulationWorker:WorldDataPath"] = WorldDataRootPath(),
            ["SimulationWorker:UdpPort"] = "27015",
            ["SimulationWorker:AdvertisedHost"] = "127.0.0.1",
            ["SimulationWorker:AdvertisedUdpPort"] = "27015",
            ["SimulationWorker:MaxConnections"] = "100",
            ["SimulationWorker:JoinHandshakeTimeoutSeconds"] = "10",
            ["SimulationWorker:NetworkPollIntervalMilliseconds"] = "15",
            ["SimulationWorker:GameServiceBaseUrl"] = "http://localhost:5000",
            ["SimulationWorker:GameServiceTimeoutSeconds"] = "5",
            ["SimulationWorker:GameServiceSecret"] = "test-simulation-worker-secret-at-least-32-characters",
            ["SimulationWorker:SimulationSessionHeartbeatSeconds"] = "10",
            ["SimulationWorker:SimulationSessionHeartbeatMaxConcurrency"] = "8",
            ["SimulationWorker:RegistryHeartbeatSeconds"] = "10",
            ["SimulationWorker:NetworkMetricsLogSeconds"] = "30",
            ["SimulationWorker:UdpQuotas:InboundPacketsPerSecond"] = "120",
            ["SimulationWorker:UdpQuotas:InboundPacketBurst"] = "240",
            ["SimulationWorker:UdpQuotas:InboundBytesPerSecond"] = "131072",
            ["SimulationWorker:UdpQuotas:InboundByteBurst"] = "262144",
            ["SimulationWorker:UdpQuotas:SnapshotBytesPerSecond"] = "262144",
            ["SimulationWorker:UdpQuotas:SnapshotByteBurst"] = "524288",
            ["SimulationWorker:UdpQuotas:AggregateSnapshotBytesPerSecond"] = "39845888",
            ["SimulationWorker:UdpQuotas:AggregateSnapshotByteBurst"] = "4194304",
            ["SimulationWorker:InterestManagement:CellSize"] = "64",
            ["SimulationWorker:InterestManagement:EnterRadius"] = "128",
            ["SimulationWorker:InterestManagement:ExitRadius"] = "144",
            ["SimulationWorker:CollisionStreaming:LoadRadiusChunks"] = "2",
            ["SimulationWorker:CollisionStreaming:UnloadRadiusChunks"] = "3",
            ["SimulationWorker:Movement:TickRateHz"] = "30",
            ["SimulationWorker:Movement:SnapshotRateHz"] = "15",
            ["SimulationWorker:Movement:WalkSpeed"] = "5",
            ["SimulationWorker:Movement:RotationSpeedDegrees"] = "720",
            ["SimulationWorker:Movement:Gravity"] = "-24",
            ["SimulationWorker:Movement:MaximumFallSpeed"] = "55",
            ["SimulationWorker:Movement:GroundedVerticalVelocity"] = "-2",
            ["SimulationWorker:Movement:CharacterRadius"] = "0.35",
            ["SimulationWorker:Movement:CharacterHeight"] = "2",
            ["SimulationWorker:Movement:StepHeight"] = "0.35",
            ["SimulationWorker:Movement:MaximumSlopeDegrees"] = "45",
            ["SimulationWorker:Movement:GroundSnapDistance"] = "0.4",
            ["SimulationWorker:Movement:MaximumSubstepDistance"] = "0.1",
            ["SimulationWorker:Movement:MaximumPenetrationIterations"] = "6",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000"
        };
    }

    private static string WorldDataRootPath()
    {
        return RepositoryPaths.Worlds;
    }
}
