namespace WorldServer.Config;

public sealed record WorldServerConfig(
    string WorldServerId,
    int UdpPort,
    string HttpUrl,
    Uri AuthServiceBaseUrl,
    TimeSpan AuthServiceTimeout,
    TimeSpan WorldSessionHeartbeatInterval,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout)
{
    public static WorldServerConfig FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("WorldServer");

        return new WorldServerConfig(
            GetString("WORLD_SERVER_ID", section["WorldServerId"], "local-world-1"),
            GetInt("WORLD_UDP_PORT", section["UdpPort"], 27015),
            GetString("WORLD_HTTP_URL", section["HttpUrl"], "http://localhost:5100"),
            GetUri("AUTH_SERVICE_BASE_URL", section["AuthServiceBaseUrl"], "http://localhost:5000"),
            TimeSpan.FromSeconds(GetInt("AUTH_SERVICE_TIMEOUT_SECONDS", section["AuthServiceTimeoutSeconds"], 5)),
            TimeSpan.FromSeconds(GetInt(
                "WORLD_SESSION_HEARTBEAT_SECONDS",
                section["WorldSessionHeartbeatSeconds"],
                10)),
            GetString("ConnectionStrings__Redis", configuration.GetConnectionString("Redis"), "localhost:6379"),
            TimeSpan.FromMilliseconds(GetInt(
                "HealthChecks__TcpTimeoutMilliseconds",
                configuration["HealthChecks:TcpTimeoutMilliseconds"],
                1_000)));
    }

    private static string GetString(string environmentName, string? configuredValue, string defaultValue)
    {
        var environmentValue = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue;
    }

    private static int GetInt(string environmentName, string? configuredValue, int defaultValue)
    {
        var value = GetString(environmentName, configuredValue, defaultValue.ToString());
        return int.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
    }

    private static Uri GetUri(string environmentName, string? configuredValue, string defaultValue)
    {
        var value = GetString(environmentName, configuredValue, defaultValue);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : new Uri(defaultValue, UriKind.Absolute);
    }
}
