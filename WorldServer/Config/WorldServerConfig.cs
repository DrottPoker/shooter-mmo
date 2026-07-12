using ShooterMmo.Shared.Networking;

namespace WorldServer.Config;

public sealed record WorldServerConfig(
    string WorldServerId,
    int UdpPort,
    string HttpUrl,
    Uri AuthServiceBaseUrl,
    TimeSpan AuthServiceTimeout,
    string AuthServiceSecret,
    TimeSpan WorldSessionHeartbeatInterval,
    TimeSpan WorldRegistryHeartbeatInterval,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout)
{
    public static WorldServerConfig FromConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>();
        var section = configuration.GetSection("WorldServer");

        var worldServerId = Require(
            First(section["WorldServerId"], configuration["WORLD_SERVER_ID"]),
            "WorldServer:WorldServerId",
            errors);
        var httpUrl = Require(
            First(section["HttpUrl"], configuration["WORLD_HTTP_URL"]),
            "WorldServer:HttpUrl",
            errors);
        var authServiceUrl = Require(
            First(section["AuthServiceBaseUrl"], configuration["AUTH_SERVICE_BASE_URL"]),
            "WorldServer:AuthServiceBaseUrl",
            errors);
        var authServiceSecret = Require(
            First(section["AuthServiceSecret"], configuration["WORLD_SERVER_SERVICE_SECRET"]),
            "WorldServer:AuthServiceSecret",
            errors);
        var redis = Require(configuration.GetConnectionString("Redis"), "ConnectionStrings:Redis", errors);

        var udpPort = PositiveInt(
            First(section["UdpPort"], configuration["WORLD_UDP_PORT"]),
            "WorldServer:UdpPort",
            errors);
        var authTimeoutSeconds = PositiveInt(
            First(section["AuthServiceTimeoutSeconds"], configuration["AUTH_SERVICE_TIMEOUT_SECONDS"]),
            "WorldServer:AuthServiceTimeoutSeconds",
            errors);
        var sessionHeartbeatSeconds = PositiveInt(
            First(section["WorldSessionHeartbeatSeconds"], configuration["WORLD_SESSION_HEARTBEAT_SECONDS"]),
            "WorldServer:WorldSessionHeartbeatSeconds",
            errors);
        var registryHeartbeatSeconds = PositiveInt(
            First(section["WorldRegistryHeartbeatSeconds"], configuration["WORLD_REGISTRY_HEARTBEAT_SECONDS"]),
            "WorldServer:WorldRegistryHeartbeatSeconds",
            errors);
        var healthTimeoutMilliseconds = PositiveInt(
            configuration["HealthChecks:TimeoutMilliseconds"],
            "HealthChecks:TimeoutMilliseconds",
            errors);

        if (udpPort > 65535)
        {
            errors.Add("WorldServer:UdpPort must be between 1 and 65535.");
        }

        var parsedHttpUrl = ParseHttpUri(httpUrl, "WorldServer:HttpUrl", errors);
        var parsedAuthServiceUrl = ParseHttpUri(authServiceUrl, "WorldServer:AuthServiceBaseUrl", errors);

        if (authServiceSecret is not null && authServiceSecret.Length < 32)
        {
            errors.Add("WorldServer:AuthServiceSecret must be at least 32 characters.");
        }

        if (redis is not null)
        {
            try
            {
                ShooterMmo.Shared.Networking.RedisConnectionString.ParseRequiredEndpoint(redis);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add($"ConnectionStrings:Redis is invalid: {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "WorldServer configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new WorldServerConfig(
            worldServerId!,
            udpPort,
            httpUrl!,
            parsedAuthServiceUrl!,
            TimeSpan.FromSeconds(authTimeoutSeconds),
            authServiceSecret!,
            TimeSpan.FromSeconds(sessionHeartbeatSeconds),
            TimeSpan.FromSeconds(registryHeartbeatSeconds),
            redis!,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds));
    }

    private static string? Require(string? value, string key, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} is required.");
            return null;
        }

        return value.Trim();
    }

    private static string? First(string? primary, string? alias)
    {
        return string.IsNullOrWhiteSpace(alias) ? primary : alias;
    }

    private static int PositiveInt(string? value, string key, ICollection<string> errors)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            errors.Add($"{key} must be a positive integer.");
            return 1;
        }

        return parsed;
    }

    private static Uri? ParseHttpUri(string? value, string key, ICollection<string> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add($"{key} must be an absolute HTTP or HTTPS URL.");
            return null;
        }

        return uri;
    }
}
