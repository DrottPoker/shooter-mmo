using Npgsql;

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

        // Invalid settings fail startup, so only a valid connection string is rewritten.
        return new PlatformOptions(
            postgres is null || errors.Count > previousErrorCount
                ? postgres ?? string.Empty
                : ConfigurePostgresConnectionString(postgres, postgresMaximumPoolSize),
            redis ?? string.Empty,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds),
            runMigrationsOnStartup);
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
