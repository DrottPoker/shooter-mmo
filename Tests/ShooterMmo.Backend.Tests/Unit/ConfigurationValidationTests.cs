using AuthService.Config;
using Microsoft.Extensions.Configuration;
using WorldServer.Config;

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
        Assert.Contains("WORLD_SERVER_SERVICE_SECRET", exception.Message);
    }

    [Fact]
    public void AuthServiceAcceptsCompleteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(AuthSettings(includeSecrets: true))
            .Build();

        var config = AuthServiceConfig.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(30), config.WorldHeartbeatTimeout);
    }

    [Fact]
    public void WorldServerRejectsInvalidPortsAndMissingSecret()
    {
        var settings = WorldSettings();
        settings["WorldServer:UdpPort"] = "70000";
        settings["WorldServer:AdvertisedHost"] = "http://world.example.test/path";
        settings["WorldServer:AdvertisedUdpPort"] = "70001";
        settings.Remove("WorldServer:AuthServiceSecret");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorldServerConfig.FromConfiguration(configuration));

        Assert.Contains("UdpPort must be between 1 and 65535", exception.Message);
        Assert.Contains("AdvertisedUdpPort must be between 1 and 65535", exception.Message);
        Assert.Contains("AdvertisedHost must be a host name or IP address", exception.Message);
        Assert.Contains("AuthServiceSecret is required", exception.Message);
    }

    [Fact]
    public void WorldServerAcceptsCompleteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(WorldSettings())
            .Build();

        var config = WorldServerConfig.FromConfiguration(configuration);

        Assert.Equal("local-world-1", config.WorldServerId);
        Assert.Equal("127.0.0.1", config.AdvertisedHost);
        Assert.Equal(27015, config.AdvertisedUdpPort);
        Assert.Equal(100, config.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), config.WorldRegistryHeartbeatInterval);
        Assert.Equal(8, config.WorldSessionHeartbeatMaxConcurrency);
        Assert.Equal(128f, config.InterestManagement.EnterRadius);
        Assert.Equal(2, config.CollisionStreaming.LoadRadiusChunks);
        Assert.Equal(30, config.MovementSimulation.TickRateHz);
        Assert.Equal(15, config.SnapshotRateHz);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.MovementInputSilenceTimeout);
        Assert.Equal(0.35f, config.MovementSimulation.CharacterCollision.Radius);
    }

    [Fact]
    public void WorldServerRejectsUnsafeResilienceLimits()
    {
        var settings = WorldSettings();
        settings["WorldServer:WorldSessionHeartbeatMaxConcurrency"] = "129";
        settings["WorldServer:UdpQuotas:InboundPacketBurst"] = "20001";
        settings["WorldServer:InterestManagement:CellSize"] = "1";
        settings["WorldServer:InterestManagement:ExitRadius"] = "100";
        settings["WorldServer:CollisionStreaming:UnloadRadiusChunks"] = "17";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorldServerConfig.FromConfiguration(configuration));

        Assert.Contains("WorldSessionHeartbeatMaxConcurrency must not exceed 128", exception.Message);
        Assert.Contains("UDP quota rates or bursts exceed", exception.Message);
        Assert.Contains("no more than 64 searched cells", exception.Message);
        Assert.Contains("UnloadRadiusChunks must not exceed 16", exception.Message);
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
            ["WorldJoin:TicketLifetimeSeconds"] = "30",
            ["WorldSession:LeaseLifetimeSeconds"] = "30",
            ["WorldRegistry:HeartbeatTimeoutSeconds"] = "30",
            ["RateLimiting:Authentication:PermitLimit"] = "5",
            ["RateLimiting:Authentication:WindowSeconds"] = "60"
        };

        if (includeSecrets)
        {
            settings["ConnectionStrings:Postgres"] =
                "Host=localhost;Port=5432;Database=game;Username=game;Password=test-password";
            settings["ServiceAuthentication:WorldServers:local-world-1"] =
                "test-world-server-secret-at-least-32-characters";
        }

        return settings;
    }

    private static Dictionary<string, string?> WorldSettings()
    {
        return new Dictionary<string, string?>
        {
            ["WorldServer:WorldServerId"] = "local-world-1",
            ["WorldServer:CollisionDataPath"] = "CollisionData",
            ["WorldServer:UdpPort"] = "27015",
            ["WorldServer:AdvertisedHost"] = "127.0.0.1",
            ["WorldServer:AdvertisedUdpPort"] = "27015",
            ["WorldServer:MaxConnections"] = "100",
            ["WorldServer:JoinHandshakeTimeoutSeconds"] = "10",
            ["WorldServer:NetworkPollIntervalMilliseconds"] = "15",
            ["WorldServer:AuthServiceBaseUrl"] = "http://localhost:5000",
            ["WorldServer:AuthServiceTimeoutSeconds"] = "5",
            ["WorldServer:AuthServiceSecret"] = "test-world-server-secret-at-least-32-characters",
            ["WorldServer:WorldSessionHeartbeatSeconds"] = "10",
            ["WorldServer:WorldSessionHeartbeatMaxConcurrency"] = "8",
            ["WorldServer:WorldRegistryHeartbeatSeconds"] = "10",
            ["WorldServer:NetworkMetricsLogSeconds"] = "30",
            ["WorldServer:UdpQuotas:InboundPacketsPerSecond"] = "120",
            ["WorldServer:UdpQuotas:InboundPacketBurst"] = "240",
            ["WorldServer:UdpQuotas:InboundBytesPerSecond"] = "131072",
            ["WorldServer:UdpQuotas:InboundByteBurst"] = "262144",
            ["WorldServer:UdpQuotas:SnapshotBytesPerSecond"] = "262144",
            ["WorldServer:UdpQuotas:SnapshotByteBurst"] = "524288",
            ["WorldServer:InterestManagement:CellSize"] = "64",
            ["WorldServer:InterestManagement:EnterRadius"] = "128",
            ["WorldServer:InterestManagement:ExitRadius"] = "144",
            ["WorldServer:CollisionStreaming:LoadRadiusChunks"] = "2",
            ["WorldServer:CollisionStreaming:UnloadRadiusChunks"] = "3",
            ["WorldServer:Movement:TickRateHz"] = "30",
            ["WorldServer:Movement:SnapshotRateHz"] = "15",
            ["WorldServer:Movement:InputSilenceTimeoutMilliseconds"] = "500",
            ["WorldServer:Movement:WalkSpeed"] = "5",
            ["WorldServer:Movement:SprintSpeed"] = "8",
            ["WorldServer:Movement:RotationSpeedDegrees"] = "720",
            ["WorldServer:Movement:Gravity"] = "-24",
            ["WorldServer:Movement:MaximumFallSpeed"] = "55",
            ["WorldServer:Movement:JumpVelocity"] = "7",
            ["WorldServer:Movement:GroundedVerticalVelocity"] = "-2",
            ["WorldServer:Movement:GroundHeight"] = "0",
            ["WorldServer:Movement:MinimumX"] = "-14",
            ["WorldServer:Movement:MaximumX"] = "14",
            ["WorldServer:Movement:MinimumZ"] = "-14",
            ["WorldServer:Movement:MaximumZ"] = "14",
            ["WorldServer:Movement:CharacterRadius"] = "0.35",
            ["WorldServer:Movement:CharacterHeight"] = "2",
            ["WorldServer:Movement:StepHeight"] = "0.35",
            ["WorldServer:Movement:MaximumSlopeDegrees"] = "45",
            ["WorldServer:Movement:GroundSnapDistance"] = "0.4",
            ["WorldServer:Movement:MaximumSubstepDistance"] = "0.1",
            ["WorldServer:Movement:MaximumPenetrationIterations"] = "6",
            ["WorldServer:Movement:SpawnX"] = "0",
            ["WorldServer:Movement:SpawnY"] = "0",
            ["WorldServer:Movement:SpawnZ"] = "-1",
            ["WorldServer:Movement:SpawnYawDegrees"] = "0",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000"
        };
    }
}
