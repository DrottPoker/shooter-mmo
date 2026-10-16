using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Items;
using Npgsql;

namespace Mmorpg.GameService.Tests;

public sealed class GameServiceConfigurationValidationTests
{
    [Fact]
    public void RedisAccelerationDefaultsAreSafeAndBounded()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = RedisAccelerationOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.True(options.SessionCacheEnabled);
        Assert.Equal(TimeSpan.FromSeconds(60), options.SessionCacheTtl);
        Assert.True(options.SessionRevocationTombstoneLifetime >= options.SessionCacheTtl);
    }

    [Fact]
    public void RedisAccelerationRejectsTombstonesShorterThanCacheEntries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisAcceleration:SessionCacheTtlSeconds"] = "60",
                ["RedisAcceleration:SessionRevocationTombstoneSeconds"] = "30"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RedisAccelerationOptions.FromConfiguration(configuration));

        Assert.Contains("at least SessionCacheTtlSeconds", exception.Message);
    }

    [Fact]
    public void GameServiceRejectsMissingSecretsAndConnections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(AuthSettings(includeSecrets: false))
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => GameServiceCoreConfiguration.FromConfiguration(configuration));

        Assert.Contains("ConnectionStrings:Postgres is required", exception.Message);
        Assert.Contains("SIMULATION_WORKER_SERVICE_SECRET", exception.Message);
    }

    [Fact]
    public void GameServiceAcceptsCompleteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(AuthSettings(includeSecrets: true))
            .Build();

        var config = GameServiceCoreConfiguration.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(30), config.Placement.WorkerHeartbeatTimeout);
        var postgres = new NpgsqlConnectionStringBuilder(config.Platform.PostgresConnectionString);
        Assert.Equal(64, postgres.MaxPoolSize);
        Assert.Equal("Mmorpg.GameService", postgres.ApplicationName);
    }

    [Fact]
    public void PlacementMetricsIntervalIsOptionalAndBounded()
    {
        var defaults = GameServiceCoreConfiguration.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(AuthSettings(includeSecrets: true))
            .Build());
        var settings = AuthSettings(includeSecrets: true);
        settings["Simulation:MetricsIntervalSeconds"] = "0";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GameServiceCoreConfiguration.FromConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build()));

        Assert.Equal(TimeSpan.FromSeconds(15), defaults.Placement.MetricsInterval);
        Assert.Contains("Simulation:MetricsIntervalSeconds must be an integer from 1 to 3600", exception.Message);
    }

    [Theory]
    [InlineData("0", "positive integer")]
    [InlineData("1001", "must not exceed")]
    public void GameServiceRejectsUnsafePostgresPoolSize(
        string maximumPoolSize,
        string expectedMessage)
    {
        var settings = AuthSettings(includeSecrets: true);
        settings["Database:MaximumPoolSize"] = maximumPoolSize;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => GameServiceCoreConfiguration.FromConfiguration(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Auth:SessionLifetimeHours", "100000000", "from 1 to 720")]
    [InlineData("RateLimiting:Authentication:WindowSeconds", "0", "from 1 to 3600")]
    [InlineData("RateLimiting:Authentication:PermitLimit", "1001", "from 1 to 1000")]
    public void GameServiceRejectsOutOfRangeAuthenticationLimits(
        string key,
        string value,
        string expectedMessage)
    {
        var settings = AuthSettings(includeSecrets: true);
        settings[key] = value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => GameServiceCoreConfiguration.FromConfiguration(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameServiceRejectsThePlaceholderWorkerSecret()
    {
        var settings = AuthSettings(includeSecrets: true);
        settings.Remove("ServiceAuthentication:SimulationWorkers:local-simulation-worker-1");
        settings["SIMULATION_WORKER_SERVICE_SECRET"] =
            "replace-with-a-long-local-simulation-worker-secret";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => GameServiceCoreConfiguration.FromConfiguration(configuration));

        Assert.Contains("placeholder", exception.Message);
    }

    [Fact]
    public void GameServiceAcceptsSharedWorkerSecretWithoutEnvironmentWorkerIdentity()
    {
        var settings = AuthSettings(includeSecrets: true);
        settings.Remove("ServiceAuthentication:SimulationWorkers:local-simulation-worker-1");
        settings["SIMULATION_WORKER_SERVICE_SECRET"] =
            "test-shared-simulation-secret-at-least-32-characters";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var config = GameServiceCoreConfiguration.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(30), config.Placement.WorkerHeartbeatTimeout);
    }

    [Fact]
    public void StackStressFixturesRequireDevelopmentLoopbackTestDatabaseAndSecret()
    {
        var settings = new Dictionary<string, string?>
        {
            ["StackStressFixtures:Enabled"] = "true",
            [StackStressFixtureOptions.SecretEnvironmentVariable] =
                "test-stack-stress-fixture-secret-at-least-32-characters"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var options = StackStressFixtureOptions.FromConfiguration(
            configuration,
            isDevelopment: true,
            "Host=127.0.0.1;Database=shooter_mmo_stack_stress_test;Username=test;Password=test");

        Assert.True(options.Enabled);

        var production = Assert.Throws<InvalidOperationException>(() =>
            StackStressFixtureOptions.FromConfiguration(
                configuration,
                isDevelopment: false,
                "Host=database.example;Database=game;Username=test;Password=test"));
        Assert.Contains("Development", production.Message, StringComparison.Ordinal);
        Assert.Contains("loopback", production.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stress", production.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NpcItemLifecycleRequiresPositiveServerOwnedInsurancePrice()
    {
        var validConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Items:NpcLifecycle:InsurancePrice"] = "175",
                ["Items:NpcLifecycle:QuestGrantId"] = "quest.test.grant",
                ["Items:NpcLifecycle:QuestItemDefinitionId"] = "quest_item.test",
                ["Items:NpcLifecycle:QuestItemQuantity"] = "1"
            })
            .Build();

        var options = NpcItemLifecycleOptions.FromConfiguration(validConfiguration);
        Assert.Equal(175, options.InsurancePrice);

        var invalidConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Items:NpcLifecycle:InsurancePrice"] = "0"
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() =>
            NpcItemLifecycleOptions.FromConfiguration(invalidConfiguration));
    }

    [Fact]
    public void GameServiceRejectsInvalidTopologyAndWorkerCredentialIdentity()
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
            () => GameServiceCoreConfiguration.FromConfiguration(configuration));

        Assert.Contains("must use a valid worker identifier", exception.Message);
        Assert.Contains("FleetId references an unknown fleet", exception.Message);
    }

    private static Dictionary<string, string?> AuthSettings(bool includeSecrets)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000",
            ["Database:RunMigrationsOnStartup"] = "true",
            ["Database:MaximumPoolSize"] = "64",
            ["Auth:SessionLifetimeHours"] = "24",
            ["Game:MaxCharactersPerAccount"] = "5",
            ["Simulation:JoinTicketLifetimeSeconds"] = "30",
            ["Simulation:SessionLeaseLifetimeSeconds"] = "30",
            ["Simulation:WorkerHeartbeatTimeoutSeconds"] = "30",
            ["Simulation:Topology:Fleets:0:Id"] = "local-fleet",
            ["Simulation:Topology:Fleets:0:DisplayName"] = "Local Development",
            ["Simulation:Topology:Fleets:0:RegionCode"] = "LOCAL",
            ["Simulation:Topology:Nodes:0:Id"] = "local-node-1",
            ["Simulation:Topology:Nodes:0:FleetId"] = "local-fleet",
            ["Simulation:Topology:Nodes:0:DisplayName"] = "Local Node 1",
            ["Simulation:Topology:Shards:0:Id"] = "local-shard-1",
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
}
