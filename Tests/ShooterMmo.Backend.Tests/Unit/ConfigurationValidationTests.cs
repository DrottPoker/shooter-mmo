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
        Assert.Equal(TimeSpan.FromSeconds(10), config.WorldRegistryHeartbeatInterval);
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
            ["WorldServer:UdpPort"] = "27015",
            ["WorldServer:HttpUrl"] = "http://localhost:5100",
            ["WorldServer:AuthServiceBaseUrl"] = "http://localhost:5000",
            ["WorldServer:AuthServiceTimeoutSeconds"] = "5",
            ["WorldServer:AuthServiceSecret"] = "test-world-server-secret-at-least-32-characters",
            ["WorldServer:WorldSessionHeartbeatSeconds"] = "10",
            ["WorldServer:WorldRegistryHeartbeatSeconds"] = "10",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000"
        };
    }
}
