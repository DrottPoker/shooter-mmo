using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Retention;
using Npgsql;
using StackExchange.Redis;

namespace Mmorpg.GameService.Platform.Configuration;

// Process-wide GameService settings shared by every module.
public sealed record PlatformOptions(
    string PostgresConnectionString,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout,
    bool RunMigrationsOnStartup)
{
    public const int MaximumPostgresPoolSizeLimit = 1_000;

    // PostgreSQL sees every GameService connection under this application name.
    public const string PostgresApplicationName = "Mmorpg.GameService";

    public const string MigrationLockTimeoutKey = "Database:MigrationLockTimeoutMilliseconds";
    public const int DefaultMigrationLockTimeoutMilliseconds = 5_000;

    public const string RedisCommandTimeoutKey = "Redis:CommandTimeoutMilliseconds";
    public const int DefaultRedisCommandTimeoutMilliseconds = 2_000;

    // How long the Redis client waits for a connection or a command's answer, apart from the
    // readiness probe's timeout; a caller may wait less.
    public TimeSpan RedisCommandTimeout { get; init; } =
        TimeSpan.FromMilliseconds(DefaultRedisCommandTimeoutMilliseconds);

    // How long a migration in a transaction waits for a table lock before it fails, so it never
    // queues behind live traffic and blocks everything behind itself.
    public TimeSpan MigrationLockTimeout { get; init; } =
        TimeSpan.FromMilliseconds(DefaultMigrationLockTimeoutMilliseconds);

    // The Retention section, which the retention host and every module's retention tasks share.
    public RetentionOptions Retention { get; init; } = RetentionOptions.Default;

    // The Http section: trusted proxies, Kestrel's limits, and the orderly stop.
    public HttpServerOptions Http { get; init; } = HttpServerOptions.Default;

    // The per-address limit of the anonymous routes without a limit of their own.
    public FixedWindowRateLimit PublicRateLimit { get; init; } = PublicRateLimitPolicy.Defaults;

    public static PlatformOptions Read(IConfiguration configuration, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        var previousErrorCount = errors.Count;
        var postgres = ConfigurationValidation.RequireConnectionString(configuration, "Postgres", errors);
        var redis = ConfigurationValidation.RequireConnectionString(configuration, "Redis", errors);

        ValidatePostgres(postgres, errors);
        ValidateRedis(redis, errors);
        var runMigrationsOnStartup = ConfigurationValidation.ValidateBoolean(
            configuration,
            "Database:RunMigrationsOnStartup",
            errors);
        var migrationLockTimeoutMilliseconds = configuration[MigrationLockTimeoutKey] is null
            ? DefaultMigrationLockTimeoutMilliseconds
            : ConfigurationValidation.ValidateIntRange(
                configuration,
                MigrationLockTimeoutKey,
                100,
                600_000,
                errors);
        var postgresMaximumPoolSize = ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "Database:MaximumPoolSize",
            errors);
        if (postgresMaximumPoolSize > MaximumPostgresPoolSizeLimit)
        {
            errors.Add(
                $"Database:MaximumPoolSize must not exceed {MaximumPostgresPoolSizeLimit}.");
        }

        var healthTimeoutMilliseconds = ConfigurationValidation.ValidatePositiveInt(
            configuration,
            "HealthChecks:TimeoutMilliseconds",
            errors);
        var redisCommandTimeoutMilliseconds = configuration[RedisCommandTimeoutKey] is null
            ? DefaultRedisCommandTimeoutMilliseconds
            : ConfigurationValidation.ValidateIntRange(
                configuration,
                RedisCommandTimeoutKey,
                100,
                60_000,
                errors);
        var retention = RetentionOptions.Read(configuration, errors);
        var http = HttpServerOptions.Read(configuration, errors);
        var publicRateLimit = FixedWindowRateLimit.Read(
            configuration,
            PublicRateLimitPolicy.Section,
            PublicRateLimitPolicy.Defaults,
            PublicRateLimitPolicy.MaximumPermitLimit,
            errors);

        // Invalid settings fail startup, so only a valid connection string is rewritten.
        return new PlatformOptions(
            postgres is null || errors.Count > previousErrorCount
                ? postgres ?? string.Empty
                : ConfigurePostgresConnectionString(postgres, postgresMaximumPoolSize),
            redis ?? string.Empty,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds),
            runMigrationsOnStartup)
        {
            MigrationLockTimeout = TimeSpan.FromMilliseconds(migrationLockTimeoutMilliseconds),
            RedisCommandTimeout = TimeSpan.FromMilliseconds(redisCommandTimeoutMilliseconds),
            Retention = retention,
            Http = http,
            PublicRateLimit = publicRateLimit
        };
    }

    // The rules that only the Development environment may break: real host names, and real
    // passwords for PostgreSQL and Redis instead of none or one that the repository publishes.
    public IReadOnlyList<string> ErrorsOutsideDevelopment()
    {
        var errors = new List<string>(Http.ErrorsOutsideDevelopment());
        if (ConfigurationValidation.IsPlaceholderPassword(PostgresPassword(PostgresConnectionString)))
        {
            errors.Add(
                "ConnectionStrings:Postgres has an empty or placeholder password; outside the Development environment it needs a real secret, never one of .env.example or the repository's Compose files.");
        }

        if (ConfigurationValidation.IsPlaceholderPassword(RedisPassword(RedisConnectionString)))
        {
            errors.Add(
                "ConnectionStrings:Redis has no password or a placeholder one; outside the Development environment Redis needs a real secret in password=.");
        }

        return errors;
    }

    private static string? PostgresPassword(string connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Password;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? RedisPassword(string connectionString)
    {
        try
        {
            return ConfigurationOptions.Parse(connectionString).Password;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ConfigurePostgresConnectionString(
        string connectionString,
        int maximumPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = PostgresApplicationName,
            MaxPoolSize = maximumPoolSize
        };
        return builder.ConnectionString;
    }

    private static void ValidatePostgres(string? connectionString, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host)
                || string.IsNullOrWhiteSpace(builder.Database)
                || string.IsNullOrWhiteSpace(builder.Username)
                || string.IsNullOrWhiteSpace(builder.Password))
            {
                errors.Add("ConnectionStrings:Postgres must include host, database, username, and password.");
            }
        }
        catch (ArgumentException exception)
        {
            errors.Add($"ConnectionStrings:Postgres is invalid: {exception.Message}");
        }
    }

    private static void ValidateRedis(string? connectionString, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            // Qualified because the record property has the same name.
            Mmorpg.Server.Common.Networking.RedisConnectionString.ParseRequiredEndpoint(connectionString);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add($"ConnectionStrings:Redis is invalid: {exception.Message}");
        }
    }
}
