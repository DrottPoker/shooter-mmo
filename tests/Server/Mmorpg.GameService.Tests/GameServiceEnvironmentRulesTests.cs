using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService.Config;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests;

// Outside Development GameService refuses to start with settings that only suit a local stack:
// any host, the shared worker secret, and empty or published passwords. Development keeps them.
public sealed class GameServiceEnvironmentRulesTests
{
    [Fact]
    public void ProductionStartsWithRealHostsPasswordsAndOneSecretPerWorker()
    {
        var configuration = Load(Environments.Production, GameServiceHostSettings.Required());

        Assert.Equal("localhost;127.0.0.1", configuration.Platform.Http.AllowedHosts);
    }

    [Theory]
    [InlineData("AllowedHosts", null, "AllowedHosts must name the host names of GameService")]
    [InlineData("AllowedHosts", "*", "'*' is allowed only in the Development environment")]
    [InlineData("SIMULATION_WORKER_SERVICE_SECRET", "test-shared-simulation-secret-at-least-32-characters", "give every worker its own secret in ServiceAuthentication:SimulationWorkers:<workerId>")]
    [InlineData("ConnectionStrings:Postgres", "Host=db;Database=game;Username=game;Password=replace-with-local-postgres-password", "ConnectionStrings:Postgres has an empty or placeholder password")]
    [InlineData("ConnectionStrings:Postgres", "Host=db;Database=game;Username=game;Password=stack_stress_local_only", "ConnectionStrings:Postgres has an empty or placeholder password")]
    [InlineData("ConnectionStrings:Redis", "redis:6379", "ConnectionStrings:Redis has no password or a placeholder one")]
    [InlineData("ConnectionStrings:Redis", "redis:6379,password=replace-with-redis-password", "ConnectionStrings:Redis has no password or a placeholder one")]
    public void OutsideDevelopmentLocalSettingsStopTheStart(string key, string? value, string expected)
    {
        var settings = GameServiceHostSettings.Required();
        settings[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() => Load(Environments.Production, settings));

        Assert.Contains("GameService configuration is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    // Without any worker secret the refusal names the per-worker setting too, so a production
    // operator learns which key to set.
    [Fact]
    public void AMissingWorkerSecretNamesThePerWorkerSetting()
    {
        var settings = GameServiceHostSettings.Required();
        settings.Remove("ServiceAuthentication:SimulationWorkers:local-simulation-worker-1");

        var exception = Assert.Throws<InvalidOperationException>(() => Load(Environments.Production, settings));

        Assert.Contains("ServiceAuthentication:SimulationWorkers:<workerId>", exception.Message, StringComparison.Ordinal);
    }

    // The checked-in settings and a local .env as LOCAL_DEVELOPMENT describes it: the shared
    // worker secret, a Redis without a password, and a password from a Compose file.
    [Fact]
    public void DevelopmentStillStartsWithTodaysLocalSettings()
    {
        var configurationRoot = Path.Combine(RepositoryPaths.Root, "src", "Server", "GameService", "Config");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration
            .AddJsonFile(Path.Combine(configurationRoot, "appsettings.json"))
            .AddJsonFile(Path.Combine(configurationRoot, "appsettings.Development.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=127.0.0.1;Port=5432;Database=shooter_mmo;Username=shooter_mmo;Password=stack_stress_local_only",
                ["ConnectionStrings:Redis"] = "127.0.0.1:6379",
                ["SIMULATION_WORKER_SERVICE_SECRET"] = "a-local-simulation-worker-secret-of-32-characters",
                ["WorldData:WorldsPath"] = RepositoryPaths.Worlds
            });

        var configuration = GameServiceConfiguration.Load(builder.Configuration, builder.Environment);

        Assert.Equal("*", configuration.Platform.Http.AllowedHosts);
        Assert.False(configuration.Platform.Http.TrustsProxies);
        Assert.Equal(TimeSpan.Zero, configuration.Platform.Http.ShutdownDrain);
        Assert.Equal(
            "a-local-simulation-worker-secret-of-32-characters",
            configuration.Accounts.SimulationWorkerCredentials.FindSecret("local-simulation-worker-1"));
    }

    private static GameServiceConfiguration Load(string environment, Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(settings);
        return GameServiceConfiguration.Load(builder.Configuration, builder.Environment);
    }
}
