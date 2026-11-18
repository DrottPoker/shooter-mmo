using Microsoft.Extensions.Configuration;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Tests;

// Outside Development the worker refuses settings that only suit a local stack, as GameService
// does: the shared worker secret, placeholder and published secrets and passwords, and a call
// to GameService that would carry the secret in the clear over a public address.
public sealed class SimulationWorkerEnvironmentRulesTests
{
    private const string RealRedis = "redis:6379,password=a-real-redis-secret-for-the-worker";
    private const string RealSecret = "a-real-worker-secret-of-at-least-32-characters";

    [Fact]
    public void ARealSecretAPasswordedRedisAndAPrivateGameServicePass()
    {
        var (config, configuration) = Load(Production());

        Assert.Empty(config.ErrorsOutsideDevelopment(configuration));
    }

    [Fact]
    public void TodaysLocalSettingsPassNowhereButDevelopment()
    {
        // The checked-in local stack: the shared secret from .env, a Redis without a password,
        // and the default 127.0.0.1 and localhost addresses.
        var settings = SimulationWorkerConfigurationValidationTests.WorkerSettings();
        settings.Remove("SimulationWorker:GameServiceSecret");
        settings[SimulationWorkerConfig.SharedSecretKey] = "a-local-simulation-worker-secret-of-32-characters";
        var (config, configuration) = Load(settings);

        var errors = config.ErrorsOutsideDevelopment(configuration);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.StartsWith("SIMULATION_WORKER_SERVICE_SECRET is accepted only in the Development environment", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ConnectionStrings:Redis has no password", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SimulationWorker:GameServiceSecret", "stack-stress-local-worker-secret-2026", "SimulationWorker:GameServiceSecret is a placeholder or a secret that the repository publishes")]
    [InlineData("ConnectionStrings:Redis", "redis:6379", "ConnectionStrings:Redis has no password or a placeholder one")]
    [InlineData("ConnectionStrings:Redis", "redis:6379,password=replace-with-redis-password", "ConnectionStrings:Redis has no password or a placeholder one")]
    [InlineData("ConnectionStrings:Redis", "redis:6379,password=shooter_mmo_tests_password", "ConnectionStrings:Redis has no password or a placeholder one")]
    [InlineData("SimulationWorker:AdvertisedHost", "0.0.0.0", "SimulationWorker:AdvertisedHost is a wildcard address")]
    [InlineData("SimulationWorker:AdvertisedHost", "::", "SimulationWorker:AdvertisedHost is a wildcard address")]
    [InlineData("SimulationWorker:GameServiceBaseUrl", "http://203.0.113.10:8080", "plain HTTP to a public address")]
    [InlineData("SimulationWorker:GameServiceBaseUrl", "http://[2001:db8::1]:8080", "plain HTTP to a public address")]
    [InlineData(SimulationWorkerConfig.SharedSecretKey, "a-local-simulation-worker-secret-of-32-characters", "give the worker its own secret in SimulationWorker:GameServiceSecret")]
    public void ASettingThatOnlySuitsALocalStackIsRefused(string key, string value, string expected)
    {
        var settings = Production();
        settings[key] = value;
        var (config, configuration) = Load(settings);

        var error = Assert.Single(config.ErrorsOutsideDevelopment(configuration));

        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://game-service:8080")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://10.0.4.7:8080")]
    [InlineData("http://172.20.0.3:8080")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("http://169.254.10.1:8080")]
    [InlineData("http://[::1]:5000")]
    [InlineData("http://[fd00::5]:8080")]
    [InlineData("http://game-service.example.com:8080")]
    [InlineData("https://203.0.113.10:8443")]
    public void APrivateAddressANameOrTlsMayCarryTheWorkerCall(string url)
    {
        var settings = Production();
        settings["SimulationWorker:GameServiceBaseUrl"] = url;
        var (config, configuration) = Load(settings);

        Assert.Empty(config.ErrorsOutsideDevelopment(configuration));
    }

    [Fact]
    public void EveryProblemIsListedTogether()
    {
        var settings = Production();
        settings["SimulationWorker:GameServiceSecret"] = "stack-stress-local-worker-secret-2026";
        settings["ConnectionStrings:Redis"] = "redis:6379";
        settings["SimulationWorker:AdvertisedHost"] = "0.0.0.0";
        var (config, configuration) = Load(settings);

        Assert.Equal(3, config.ErrorsOutsideDevelopment(configuration).Count);
    }

    // The health file is optional, so a worker without it keeps writing none.
    [Fact]
    public void TheHealthFilePathIsOptionalAndResolvedToAFullPath()
    {
        var settings = Production();
        var unset = Load(settings).Config;
        settings["SimulationWorker:HealthFilePath"] = "health/worker-ready";
        var set = Load(settings).Config;

        Assert.Null(unset.HealthFilePath);
        Assert.Equal(Path.GetFullPath("health/worker-ready"), set.HealthFilePath);
    }

    private static Dictionary<string, string?> Production()
    {
        var settings = SimulationWorkerConfigurationValidationTests.WorkerSettings();
        settings["SimulationWorker:GameServiceSecret"] = RealSecret;
        settings["SimulationWorker:GameServiceBaseUrl"] = "http://game-service:8080";
        settings["ConnectionStrings:Redis"] = RealRedis;
        return settings;
    }

    private static (SimulationWorkerConfig Config, IConfiguration Configuration) Load(
        Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (SimulationWorkerConfig.FromConfiguration(configuration), configuration);
    }
}
