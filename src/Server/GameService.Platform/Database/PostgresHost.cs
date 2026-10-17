using System.Net;
using Npgsql;

namespace Mmorpg.GameService.Platform.Database;

public static class PostgresHost
{
    // Development tools and stress fixtures write durable rows directly, so they accept only
    // a PostgreSQL host on this machine.
    public static bool IsLoopback(NpgsqlConnectionStringBuilder connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var host = connection.Host?.Trim() ?? string.Empty;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
