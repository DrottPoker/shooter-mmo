using Mmorpg.GameService.Accounts.PasswordHashing;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Accounts;

// An account session ends at the earlier of its last activity plus SessionIdleLifetime and its
// login plus SessionMaximumLifetime, unless a live simulation session holds it.
public sealed record AccountsModuleOptions(
    TimeSpan SessionIdleLifetime,
    TimeSpan SessionMaximumLifetime,
    int AuthenticationPermitLimit,
    TimeSpan AuthenticationRateLimitWindow,
    SimulationWorkerCredentials SimulationWorkerCredentials)
{
    public const string SessionIdleLifetimeDaysKey = "Auth:SessionIdleLifetimeDays";
    public const string SessionMaximumLifetimeDaysKey = "Auth:SessionMaximumLifetimeDays";
    public const string RemovedSessionLifetimeHoursKey = "Auth:SessionLifetimeHours";
    public const string AccountActionsRateLimitSection = "RateLimiting:AccountActions";
    public const string AccountReadsRateLimitSection = "RateLimiting:AccountReads";
    public const int MaximumAccountRateLimitPermits = 100_000;

    public static FixedWindowRateLimit DefaultAccountActionsRateLimit { get; } = new(20, TimeSpan.FromSeconds(60));

    public static FixedWindowRateLimit DefaultAccountReadsRateLimit { get; } = new(300, TimeSpan.FromSeconds(60));

    // Read by its own factory after the core settings are valid.
    public RedisAccelerationOptions RedisAcceleration { get; init; } = RedisAccelerationOptions.Disabled;

    // The password hashing lane; optional settings with defaults.
    public PasswordHashingOptions PasswordHashing { get; init; } = PasswordHashingOptions.Default;

    // Requests per account and window of each route that creates work or state.
    public FixedWindowRateLimit AccountActionsRateLimit { get; init; } = DefaultAccountActionsRateLimit;

    // Requests per account and window of the routes that read, together.
    public FixedWindowRateLimit AccountReadsRateLimit { get; init; } = DefaultAccountReadsRateLimit;

    public static AccountsModuleOptions ReadCore(
        IConfiguration configuration,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        var sessionIdleLifetimeDays = ConfigurationValidation.ValidateIntRange(
            configuration,
            SessionIdleLifetimeDaysKey,
            1,
            365,
            errors);
        var sessionMaximumLifetimeDays = ConfigurationValidation.ValidateIntRange(
            configuration,
            SessionMaximumLifetimeDaysKey,
            1,
            3_650,
            errors);
        if (sessionMaximumLifetimeDays < sessionIdleLifetimeDays)
        {
            errors.Add($"{SessionMaximumLifetimeDaysKey} must be at least {SessionIdleLifetimeDaysKey}.");
        }

        // A removed key fails the start instead of being ignored: a deployment that still sets
        // it would believe its logins end after that many hours.
        if (configuration[RemovedSessionLifetimeHoursKey] is not null)
        {
            errors.Add(
                $"{RemovedSessionLifetimeHoursKey} was removed; {SessionIdleLifetimeDaysKey} and {SessionMaximumLifetimeDaysKey} set how long a login lasts.");
        }

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
        var passwordHashing = PasswordHashingOptions.Read(configuration, errors);
        var accountActionsRateLimit = FixedWindowRateLimit.Read(
            configuration,
            AccountActionsRateLimitSection,
            DefaultAccountActionsRateLimit,
            MaximumAccountRateLimitPermits,
            errors);
        var accountReadsRateLimit = FixedWindowRateLimit.Read(
            configuration,
            AccountReadsRateLimitSection,
            DefaultAccountReadsRateLimit,
            MaximumAccountRateLimitPermits,
            errors);

        return new AccountsModuleOptions(
            TimeSpan.FromDays(sessionIdleLifetimeDays),
            TimeSpan.FromDays(sessionMaximumLifetimeDays),
            authenticationPermitLimit,
            TimeSpan.FromSeconds(authenticationWindowSeconds),
            credentials)
        {
            PasswordHashing = passwordHashing,
            AccountActionsRateLimit = accountActionsRateLimit,
            AccountReadsRateLimit = accountReadsRateLimit
        };
    }
}

// Simulation worker service secrets. Configuration keys ignore case, but worker ids
// are case-sensitive in the database, so the original key casing is kept for an
// ordinal match. The shared secret applies only when no per-worker secret exists, and only
// the Development environment accepts it.
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
                errors.Add(
                    $"{SharedSecretKey} must be at least 32 characters, or {SectionName}:<workerId> must give each worker its own secret.");
            }
            else if (ConfigurationValidation.IsPlaceholderSecret(sharedSecret))
            {
                errors.Add($"{SharedSecretKey} still has the .env.example placeholder value.");
            }
        }

        ValidateWorkerSecrets(errors);
    }

    // Outside Development every worker has its own secret, so one leaked secret names one
    // worker, and a secret that is set but would be ignored does not mislead anyone. A secret
    // that the repository's tooling publishes protects nothing either.
    public IReadOnlyList<string> ErrorsOutsideDevelopment()
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(sharedSecret))
        {
            errors.Add(
                $"{SharedSecretKey} is accepted only in the Development environment; give every worker its own secret in {SectionName}:<workerId> and remove {SharedSecretKey}.");
        }

        foreach (var (workerId, secret) in workerSecrets)
        {
            if (!string.IsNullOrWhiteSpace(secret) && ConfigurationValidation.IsRepositorySecret(secret))
            {
                errors.Add(
                    $"{SectionName}:{workerId} is a secret that the repository publishes; outside the Development environment it needs a real secret.");
            }
        }

        return errors;
    }

    private void ValidateWorkerSecrets(ICollection<string> errors)
    {
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
