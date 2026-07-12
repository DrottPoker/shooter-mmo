namespace ShooterMmo.Shared.Networking;

public static class RedisConnectionString
{
    public static ServiceEndpoint ParseRequiredEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Redis connection string is required.");
        }

        var firstEndpoint = connectionString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstEndpoint))
        {
            throw new InvalidOperationException("Redis connection string must contain an endpoint.");
        }

        var parts = firstEndpoint.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || string.IsNullOrWhiteSpace(parts[0])
            || !int.TryParse(parts[1], out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Redis connection string must use the format host:port.");
        }

        return new ServiceEndpoint(parts[0], port);
    }

    public static ServiceEndpoint ParseEndpoint(string? connectionString)
    {
        const string defaultHost = "localhost";
        const int defaultPort = 6379;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ServiceEndpoint(defaultHost, defaultPort);
        }

        var firstEndpoint = connectionString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstEndpoint))
        {
            return new ServiceEndpoint(defaultHost, defaultPort);
        }

        var parts = firstEndpoint.Split(':', 2, StringSplitOptions.TrimEntries);
        var host = string.IsNullOrWhiteSpace(parts[0]) ? defaultHost : parts[0];
        var port = parts.Length == 2 && int.TryParse(parts[1], out var parsedPort)
            ? parsedPort
            : defaultPort;

        return new ServiceEndpoint(host, port);
    }
}
