namespace ShooterMmo.Shared.Networking;

public static class PostgresConnectionString
{
    public static ServiceEndpoint ParseEndpoint(string? connectionString)
    {
        const string defaultHost = "localhost";
        const int defaultPort = 5432;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ServiceEndpoint(defaultHost, defaultPort);
        }

        var values = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var host = GetValue(values, "Host", "Server") ?? defaultHost;
        var portValue = GetValue(values, "Port");
        var port = int.TryParse(portValue, out var parsedPort) ? parsedPort : defaultPort;

        return new ServiceEndpoint(host, port);
    }

    private static string? GetValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

