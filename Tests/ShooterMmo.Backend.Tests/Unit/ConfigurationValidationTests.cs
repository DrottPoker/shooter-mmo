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
        settings.Remove("WorldServer:AuthServiceSecret");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorldServerConfig.FromConfiguration(configuration));

        Assert.Contains("UdpPort must be between 1 and 65535", exception.Message);
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
        Assert.Equal(100, config.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), config.WorldRegistryHeartbeatInterval);
        Assert.Equal(30, config.MovementSimulation.TickRateHz);
        Assert.Equal(15, config.SnapshotRateHz);
        Assert.Equal(0.35f, config.MovementSimulation.CharacterCollision.Radius);
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
            ["WorldServer:MaxConnections"] = "100",
            ["WorldServer:JoinHandshakeTimeoutSeconds"] = "10",
            ["WorldServer:NetworkPollIntervalMilliseconds"] = "15",
            ["WorldServer:AuthServiceBaseUrl"] = "http://localhost:5000",
            ["WorldServer:AuthServiceTimeoutSeconds"] = "5",
            ["WorldServer:AuthServiceSecret"] = "test-world-server-secret-at-least-32-characters",
            ["WorldServer:WorldSessionHeartbeatSeconds"] = "10",
            ["WorldServer:WorldRegistryHeartbeatSeconds"] = "10",
            ["WorldServer:Movement:TickRateHz"] = "30",
            ["WorldServer:Movement:SnapshotRateHz"] = "15",
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
