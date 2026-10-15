using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Accounts;

public sealed record AccountsModuleOptions(
    TimeSpan SessionLifetime,
    int AuthenticationPermitLimit,
    TimeSpan AuthenticationRateLimitWindow,
    SimulationWorkerCredentials SimulationWorkerCredentials)
{
    // Read by its own factory after the core settings are valid.
    public RedisAccelerationOptions RedisAcceleration { get; init; } = RedisAccelerationOptions.Disabled;

    public static AccountsModuleOptions ReadCore(
        IConfiguration configuration,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        var sessionLifetimeHours = ConfigurationValidation.ValidateIntRange(
            configuration,
            "Auth:SessionLifetimeHours",
            1,
            720,
            errors);
        var authenticationPermitLimit = ConfigurationValidation.ValidateIntRange(
            configuration,
            "RateLimiting:Authentication:PermitLimit",
            1,
            1_000,
            errors);
        var authenticationWindowSeconds = ConfigurationValidation.ValidateIntRange(
            configuration,
            "RateLimiting:Authentication:WindowSeconds",
            1,
            3_600,
            errors);
        var credentials = SimulationWorkerCredentials.FromConfiguration(configuration);
        credentials.Validate(errors);

        return new AccountsModuleOptions(
            TimeSpan.FromHours(sessionLifetimeHours),
            authenticationPermitLimit,
            TimeSpan.FromSeconds(authenticationWindowSeconds),
            credentials);
    }
}

// Simulation worker service secrets. Configuration keys ignore case, but worker ids
// are case-sensitive in the database, so the original key casing is kept for an
// ordinal match. The shared secret applies only when no per-worker secret exists.
public sealed class SimulationWorkerCredentials
{
    public const string SectionName = "ServiceAuthentication:SimulationWorkers";
    public const string SharedSecretKey = "SIMULATION_WORKER_SERVICE_SECRET";

    private readonly KeyValuePair<string, string?>[] workerSecrets;
    private readonly string? sharedSecret;

    public SimulationWorkerCredentials(
        IEnumerable<KeyValuePair<string, string?>> workerSecrets,
        string? sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(workerSecrets);
        this.workerSecrets = workerSecrets.ToArray();
        this.sharedSecret = sharedSecret;
    }

    public static SimulationWorkerCredentials FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new SimulationWorkerCredentials(
            configuration
                .GetSection(SectionName)
                .GetChildren()
                .Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)),
            configuration[SharedSecretKey]);
    }

    public string? FindSecret(string workerId)
    {
        var configuredSecret = workerSecrets
            .FirstOrDefault(entry => string.Equals(entry.Key, workerId, StringComparison.Ordinal))
            .Value;
        if (string.IsNullOrWhiteSpace(configuredSecret) && workerSecrets.Length == 0)
        {
            configuredSecret = sharedSecret;
        }

        return configuredSecret;
    }

    internal void Validate(ICollection<string> errors)
    {
        if (workerSecrets.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(sharedSecret) || sharedSecret.Length < 32)
            {
                errors.Add($"{SharedSecretKey} must be at least 32 characters.");
            }
            else if (ConfigurationValidation.IsPlaceholderSecret(sharedSecret))
            {
                errors.Add($"{SharedSecretKey} still has the .env.example placeholder value.");
            }
        }

        foreach (var (workerId, secret) in workerSecrets)
        {
            if (!ConfigurationValidation.IsValidIdentifier(workerId))
            {
                errors.Add($"{SectionName}:{workerId} must use a valid worker identifier.");
            }

            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            {
                errors.Add($"{SectionName}:{workerId} must be at least 32 characters.");
            }
            else if (ConfigurationValidation.IsPlaceholderSecret(secret))
            {
                errors.Add($"{SectionName}:{workerId} still has a placeholder value.");
            }
        }
    }
}
