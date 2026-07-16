using AuthService.Config;
using Microsoft.Extensions.Configuration;
using SimulationWorker.Config;
using SimulationWorker.Items;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void AuthServiceRejectsMissingSecretsAndConnections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(AuthSettings(includeSecrets: false))
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuthServiceConfig.FromConfiguration(configuration));

        Assert.Contains("ConnectionStrings:Postgres is required", exception.Message);
        Assert.Contains("SIMULATION_WORKER_SERVICE_SECRET", exception.Message);
    }

    [Fact]
    public void AuthServiceAcceptsCompleteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(AuthSettings(includeSecrets: true))
            .Build();

        var config = AuthServiceConfig.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(30), config.SimulationWorkerHeartbeatTimeout);
    }

    [Fact]
    public void AuthServiceRejectsInvalidTopologyAndWorkerCredentialIdentity()
    {
        var settings = AuthSettings(includeSecrets: true);
        settings.Remove("ServiceAuthentication:SimulationWorkers:local-simulation-worker-1");
        settings["ServiceAuthentication:SimulationWorkers:invalid/worker"] =
            "test-simulation-worker-secret-at-least-32-characters";
        settings["Simulation:Topology:Shards:0:FleetId"] = "missing-fleet";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuthServiceConfig.FromConfiguration(configuration));

        Assert.Contains("must use a valid worker identifier", exception.Message);
        Assert.Contains("FleetId references an unknown fleet", exception.Message);
    }

    [Fact]
    public void SimulationWorkerRejectsInvalidPortsAndMissingSecret()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:UdpPort"] = "70000";
        settings["SimulationWorker:AdvertisedHost"] = "http://world.example.test/path";
        settings["SimulationWorker:AdvertisedUdpPort"] = "70001";
        settings.Remove("SimulationWorker:AuthServiceSecret");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("UdpPort must be between 1 and 65535", exception.Message);
        Assert.Contains("AdvertisedUdpPort must be between 1 and 65535", exception.Message);
        Assert.Contains("AdvertisedHost must be a host name or IP address", exception.Message);
        Assert.Contains("AuthServiceSecret is required", exception.Message);
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
        Assert.Equal("local-world-1", config.WorldId);
        Assert.Equal("127.0.0.1", config.AdvertisedHost);
        Assert.Equal(27015, config.AdvertisedUdpPort);
        Assert.Equal(100, config.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), config.RegistryHeartbeatInterval);
        Assert.Equal(8, config.SimulationSessionHeartbeatMaxConcurrency);
        Assert.Equal(128f, config.InterestManagement.EnterRadius);
        Assert.Equal(2, config.CollisionStreaming.LoadRadiusChunks);
        Assert.Equal(38 * 1024 * 1024, config.UdpQuotas.AggregateSnapshotBytesPerSecond);
        Assert.Equal(4 * 1024 * 1024, config.UdpQuotas.AggregateSnapshotByteBurst);
        Assert.Equal(30, config.MovementSimulation.TickRateHz);
        Assert.Equal(15, config.SnapshotRateHz);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.MovementInputSilenceTimeout);
        Assert.Equal(0.35f, config.MovementSimulation.CharacterCollision.Radius);
        Assert.Equal(3, config.ItemInteraction.ServicePoints.Count);

        var access = new ItemInteractionAccessService(config);
        Assert.True(access.Evaluate(0f, 0f, -1f).Bank);
        Assert.True(access.Evaluate(0f, 0f, -1f).RecoveryStorage);
        Assert.False(access.Evaluate(0f, 0f, -1f).InsuranceNpc);
        Assert.False(access.Evaluate(4f, 0f, -1f).Bank);
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
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("SimulationSessionHeartbeatMaxConcurrency must not exceed 128", exception.Message);
        Assert.Contains("UDP quota rates or bursts exceed", exception.Message);
        Assert.Contains("aggregate snapshot quota exceeds", exception.Message);
        Assert.Contains("no more than 64 searched cells", exception.Message);
        Assert.Contains("UnloadRadiusChunks must not exceed 16", exception.Message);
    }

    [Fact]
    public void SimulationWorkerRejectsInvalidItemServicePoints()
    {
        var settings = WorkerSettings();
        settings["SimulationWorker:ItemInteraction:ServicePoints:0:Radius"] = "101";
        settings["SimulationWorker:ItemInteraction:ServicePoints:1:Id"] =
            "local_city_bank";
        settings["SimulationWorker:ItemInteraction:ServicePoints:2:Kind"] = "corpse";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationWorkerConfig.FromConfiguration(configuration));

        Assert.Contains("Radius must be greater than 0 and at most 100", exception.Message);
        Assert.Contains("Id must be unique", exception.Message);
        Assert.Contains("Kind must be bank, recovery_storage, or insurance_npc", exception.Message);
    }

    private static Dictionary<string, string?> AuthSettings(bool includeSecrets)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000",
            ["Database:RunMigrationsOnStartup"] = "true",
            ["Auth:SessionLifetimeHours"] = "24",
            ["Game:MaxCharactersPerAccount"] = "5",
            ["Simulation:JoinTicketLifetimeSeconds"] = "30",
            ["Simulation:SessionLeaseLifetimeSeconds"] = "30",
            ["Simulation:WorkerHeartbeatTimeoutSeconds"] = "30",
            ["Simulation:Topology:Worlds:0:Id"] = "local-world-1",
            ["Simulation:Topology:Worlds:0:DisplayName"] = "Local Test World",
            ["Simulation:Topology:Fleets:0:Id"] = "local-fleet",
            ["Simulation:Topology:Fleets:0:DisplayName"] = "Local Development",
            ["Simulation:Topology:Fleets:0:RegionCode"] = "LOCAL",
            ["Simulation:Topology:Nodes:0:Id"] = "local-node-1",
            ["Simulation:Topology:Nodes:0:FleetId"] = "local-fleet",
            ["Simulation:Topology:Nodes:0:DisplayName"] = "Local Node 1",
            ["Simulation:Topology:Shards:0:Id"] = "local-shard-1",
            ["Simulation:Topology:Shards:0:WorldId"] = "local-world-1",
            ["Simulation:Topology:Shards:0:FleetId"] = "local-fleet",
            ["Simulation:Topology:Shards:0:DisplayName"] = "Local Shard 1",
            ["Simulation:Topology:Shards:0:RuleSet"] = "mvp-open-risk",
            ["RateLimiting:Authentication:PermitLimit"] = "5",
            ["RateLimiting:Authentication:WindowSeconds"] = "60"
        };

        if (includeSecrets)
        {
            settings["ConnectionStrings:Postgres"] =
                "Host=localhost;Port=5432;Database=game;Username=game;Password=test-password";
            settings["ServiceAuthentication:SimulationWorkers:local-simulation-worker-1"] =
                "test-simulation-worker-secret-at-least-32-characters";
        }

        return settings;
    }

    private static Dictionary<string, string?> WorkerSettings()
    {
        return new Dictionary<string, string?>
        {
            ["SimulationWorker:SimulationWorkerId"] = "local-simulation-worker-1",
            ["SimulationWorker:FleetId"] = "local-fleet",
            ["SimulationWorker:NodeId"] = "local-node-1",
            ["SimulationWorker:ShardId"] = "local-shard-1",
            ["SimulationWorker:WorldId"] = "local-world-1",
            ["SimulationWorker:CollisionDataPath"] = "CollisionData",
            ["SimulationWorker:UdpPort"] = "27015",
            ["SimulationWorker:AdvertisedHost"] = "127.0.0.1",
            ["SimulationWorker:AdvertisedUdpPort"] = "27015",
            ["SimulationWorker:MaxConnections"] = "100",
            ["SimulationWorker:JoinHandshakeTimeoutSeconds"] = "10",
            ["SimulationWorker:NetworkPollIntervalMilliseconds"] = "15",
            ["SimulationWorker:AuthServiceBaseUrl"] = "http://localhost:5000",
            ["SimulationWorker:AuthServiceTimeoutSeconds"] = "5",
            ["SimulationWorker:AuthServiceSecret"] = "test-simulation-worker-secret-at-least-32-characters",
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
            ["SimulationWorker:ItemInteraction:ServicePoints:0:Id"] = "local_city_bank",
            ["SimulationWorker:ItemInteraction:ServicePoints:0:Kind"] = "bank",
            ["SimulationWorker:ItemInteraction:ServicePoints:0:X"] = "0",
            ["SimulationWorker:ItemInteraction:ServicePoints:0:Y"] = "0",
            ["SimulationWorker:ItemInteraction:ServicePoints:0:Z"] = "-1",
            ["SimulationWorker:ItemInteraction:ServicePoints:0:Radius"] = "3",
            ["SimulationWorker:ItemInteraction:ServicePoints:1:Id"] = "local_city_recovery",
            ["SimulationWorker:ItemInteraction:ServicePoints:1:Kind"] = "recovery_storage",
            ["SimulationWorker:ItemInteraction:ServicePoints:1:X"] = "0",
            ["SimulationWorker:ItemInteraction:ServicePoints:1:Y"] = "0",
            ["SimulationWorker:ItemInteraction:ServicePoints:1:Z"] = "-1",
            ["SimulationWorker:ItemInteraction:ServicePoints:1:Radius"] = "3",
            ["SimulationWorker:ItemInteraction:ServicePoints:2:Id"] = "local_insurance_npc",
            ["SimulationWorker:ItemInteraction:ServicePoints:2:Kind"] = "insurance_npc",
            ["SimulationWorker:ItemInteraction:ServicePoints:2:X"] = "2",
            ["SimulationWorker:ItemInteraction:ServicePoints:2:Y"] = "0",
            ["SimulationWorker:ItemInteraction:ServicePoints:2:Z"] = "-1",
            ["SimulationWorker:ItemInteraction:ServicePoints:2:Radius"] = "1",
            ["SimulationWorker:Movement:TickRateHz"] = "30",
            ["SimulationWorker:Movement:SnapshotRateHz"] = "15",
            ["SimulationWorker:Movement:InputSilenceTimeoutMilliseconds"] = "500",
            ["SimulationWorker:Movement:WalkSpeed"] = "5",
            ["SimulationWorker:Movement:SprintSpeed"] = "8",
            ["SimulationWorker:Movement:RotationSpeedDegrees"] = "720",
            ["SimulationWorker:Movement:Gravity"] = "-24",
            ["SimulationWorker:Movement:MaximumFallSpeed"] = "55",
            ["SimulationWorker:Movement:JumpVelocity"] = "7",
            ["SimulationWorker:Movement:GroundedVerticalVelocity"] = "-2",
            ["SimulationWorker:Movement:GroundHeight"] = "0",
            ["SimulationWorker:Movement:MinimumX"] = "-14",
            ["SimulationWorker:Movement:MaximumX"] = "14",
            ["SimulationWorker:Movement:MinimumZ"] = "-14",
            ["SimulationWorker:Movement:MaximumZ"] = "14",
            ["SimulationWorker:Movement:CharacterRadius"] = "0.35",
            ["SimulationWorker:Movement:CharacterHeight"] = "2",
            ["SimulationWorker:Movement:StepHeight"] = "0.35",
            ["SimulationWorker:Movement:MaximumSlopeDegrees"] = "45",
            ["SimulationWorker:Movement:GroundSnapDistance"] = "0.4",
            ["SimulationWorker:Movement:MaximumSubstepDistance"] = "0.1",
            ["SimulationWorker:Movement:MaximumPenetrationIterations"] = "6",
            ["SimulationWorker:Movement:SpawnX"] = "0",
            ["SimulationWorker:Movement:SpawnY"] = "0",
            ["SimulationWorker:Movement:SpawnZ"] = "-1",
            ["SimulationWorker:Movement:SpawnYawDegrees"] = "0",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000"
        };
    }
}
