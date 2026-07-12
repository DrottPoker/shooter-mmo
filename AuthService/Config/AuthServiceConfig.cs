using Npgsql;
using ShooterMmo.Shared.Networking;

namespace AuthService.Config;

public sealed record AuthServiceConfig(
    string PostgresConnectionString,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout,
    TimeSpan WorldHeartbeatTimeout)
{
    public static AuthServiceConfig FromConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>();
        var postgres = RequireConnectionString(configuration, "Postgres", errors);
        var redis = RequireConnectionString(configuration, "Redis", errors);

        ValidatePostgres(postgres, errors);
        ValidateRedis(redis, errors);
        ValidateBoolean(configuration, "Database:RunMigrationsOnStartup", errors);
        ValidatePositiveInt(configuration, "Auth:SessionLifetimeHours", errors);
        ValidatePositiveInt(configuration, "Game:MaxCharactersPerAccount", errors);
        ValidatePositiveInt(configuration, "WorldJoin:TicketLifetimeSeconds", errors);
        ValidatePositiveInt(configuration, "WorldSession:LeaseLifetimeSeconds", errors);
        ValidatePositiveInt(configuration, "RateLimiting:Authentication:PermitLimit", errors);
        ValidatePositiveInt(configuration, "RateLimiting:Authentication:WindowSeconds", errors);

        var healthTimeoutMilliseconds = ValidatePositiveInt(
            configuration,
            "HealthChecks:TimeoutMilliseconds",
            errors);
        var worldHeartbeatTimeoutSeconds = ValidatePositiveInt(
            configuration,
            "WorldRegistry:HeartbeatTimeoutSeconds",
            errors);

        var worldServers = configuration.GetSection("ServiceAuthentication:WorldServers").GetChildren().ToArray();
        if (worldServers.Length == 0)
        {
            var worldServerId = configuration["WORLD_SERVER_ID"];
            var worldServerSecret = configuration["WORLD_SERVER_SERVICE_SECRET"];
            if (string.IsNullOrWhiteSpace(worldServerId))
            {
                errors.Add("WORLD_SERVER_ID is required when no WorldServer credential map is configured.");
            }

            if (string.IsNullOrWhiteSpace(worldServerSecret) || worldServerSecret.Length < 32)
            {
                errors.Add("WORLD_SERVER_SERVICE_SECRET must be at least 32 characters.");
            }
        }

        foreach (var worldServer in worldServers)
        {
            if (string.IsNullOrWhiteSpace(worldServer.Key)
                || string.IsNullOrWhiteSpace(worldServer.Value)
                || worldServer.Value.Length < 32)
            {
                errors.Add($"ServiceAuthentication:WorldServers:{worldServer.Key} must be at least 32 characters.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "AuthService configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new AuthServiceConfig(
            postgres!,
            redis!,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds),
            TimeSpan.FromSeconds(worldHeartbeatTimeoutSeconds));
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
            ShooterMmo.Shared.Networking.RedisConnectionString.ParseRequiredEndpoint(connectionString);
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
}
