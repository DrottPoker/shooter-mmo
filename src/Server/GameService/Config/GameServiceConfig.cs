using Mmorpg.Server.Common.Networking;
using Npgsql;

namespace Mmorpg.GameService.Config;

public sealed record GameServiceConfig(
    string PostgresConnectionString,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout,
    TimeSpan SimulationWorkerHeartbeatTimeout,
    SimulationTopologyConfig SimulationTopology)
{
    public int AuthenticationPermitLimit { get; init; } = 5;

    public TimeSpan AuthenticationRateLimitWindow { get; init; } = TimeSpan.FromSeconds(60);

    public const int MaximumPostgresPoolSizeLimit = 1_000;

    public static GameServiceConfig FromConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>();
        var postgres = RequireConnectionString(configuration, "Postgres", errors);
        var redis = RequireConnectionString(configuration, "Redis", errors);

        ValidatePostgres(postgres, errors);
        ValidateRedis(redis, errors);
        ValidateBoolean(configuration, "Database:RunMigrationsOnStartup", errors);
        var postgresMaximumPoolSize = ValidatePositiveInt(
            configuration,
            "Database:MaximumPoolSize",
            errors);
        if (postgresMaximumPoolSize > MaximumPostgresPoolSizeLimit)
        {
            errors.Add(
                $"Database:MaximumPoolSize must not exceed {MaximumPostgresPoolSizeLimit}.");
        }
        ValidateIntRange(configuration, "Auth:SessionLifetimeHours", 1, 720, errors);
        ValidatePositiveInt(configuration, "Game:MaxCharactersPerAccount", errors);
        ValidatePositiveInt(configuration, "Simulation:JoinTicketLifetimeSeconds", errors);
        ValidatePositiveInt(configuration, "Simulation:SessionLeaseLifetimeSeconds", errors);
        var authenticationPermitLimit = ValidateIntRange(
            configuration,
            "RateLimiting:Authentication:PermitLimit",
            1,
            1_000,
            errors);
        var authenticationWindowSeconds = ValidateIntRange(
            configuration,
            "RateLimiting:Authentication:WindowSeconds",
            1,
            3_600,
            errors);

        var healthTimeoutMilliseconds = ValidatePositiveInt(
            configuration,
            "HealthChecks:TimeoutMilliseconds",
            errors);
        var workerHeartbeatTimeoutSeconds = ValidatePositiveInt(
            configuration,
            "Simulation:WorkerHeartbeatTimeoutSeconds",
            errors);

        var simulationTopology = SimulationTopologyConfig.FromConfiguration(
            configuration,
            errors);

        var simulationWorkers = configuration
            .GetSection("ServiceAuthentication:SimulationWorkers")
            .GetChildren()
            .ToArray();
        if (simulationWorkers.Length == 0)
        {
            var workerSecret = configuration["SIMULATION_WORKER_SERVICE_SECRET"];
            if (string.IsNullOrWhiteSpace(workerSecret) || workerSecret.Length < 32)
            {
                errors.Add("SIMULATION_WORKER_SERVICE_SECRET must be at least 32 characters.");
            }
            else if (IsPlaceholderSecret(workerSecret))
            {
                errors.Add("SIMULATION_WORKER_SERVICE_SECRET still has the .env.example placeholder value.");
            }
        }

        foreach (var simulationWorker in simulationWorkers)
        {
            if (!IsValidIdentifier(simulationWorker.Key))
            {
                errors.Add(
                    $"ServiceAuthentication:SimulationWorkers:{simulationWorker.Key} must use a valid worker identifier.");
            }

            if (string.IsNullOrWhiteSpace(simulationWorker.Value)
                || simulationWorker.Value.Length < 32)
            {
                errors.Add(
                    $"ServiceAuthentication:SimulationWorkers:{simulationWorker.Key} must be at least 32 characters.");
            }
            else if (IsPlaceholderSecret(simulationWorker.Value))
            {
                errors.Add(
                    $"ServiceAuthentication:SimulationWorkers:{simulationWorker.Key} still has a placeholder value.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "GameService configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new GameServiceConfig(
            ConfigurePostgresConnectionString(postgres!, postgresMaximumPoolSize),
            redis!,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds),
            TimeSpan.FromSeconds(workerHeartbeatTimeoutSeconds),
            simulationTopology)
        {
            AuthenticationPermitLimit = authenticationPermitLimit,
            AuthenticationRateLimitWindow = TimeSpan.FromSeconds(authenticationWindowSeconds)
        };
    }

    private static string ConfigurePostgresConnectionString(
        string connectionString,
        int maximumPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "Mmorpg.GameService",
            MaxPoolSize = maximumPoolSize
        };
        return builder.ConnectionString;
    }

    private static string? RequireConnectionString(
        IConfiguration configuration,
        string name,
        ICollection<string> errors)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"ConnectionStrings:{name} is required.");
        }

        return value;
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
            Mmorpg.Server.Common.Networking.RedisConnectionString.ParseRequiredEndpoint(connectionString);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add($"ConnectionStrings:Redis is invalid: {exception.Message}");
        }
    }

    private static int ValidatePositiveInt(
        IConfiguration configuration,
        string key,
        ICollection<string> errors)
    {
        var value = configuration[key];
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            errors.Add($"{key} must be a positive integer.");
            return 1;
        }

        return parsed;
    }

    // Every secret placeholder in .env.example starts with this prefix.
    public static bool IsPlaceholderSecret(string secret)
    {
        return secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase);
    }

    private static int ValidateIntRange(
        IConfiguration configuration,
        string key,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        if (!int.TryParse(configuration[key], out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            errors.Add($"{key} must be an integer from {minimum} to {maximum}.");
            return minimum;
        }

        return parsed;
    }

    private static void ValidateBoolean(
        IConfiguration configuration,
        string key,
        ICollection<string> errors)
    {
        if (!bool.TryParse(configuration[key], out _))
        {
            errors.Add($"{key} must be true or false.");
        }
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
