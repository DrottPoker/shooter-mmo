using Npgsql;

namespace Mmorpg.GameService.Items.Development;

// The development item tools write durable item state directly through the transaction
// service, so they refuse to run anywhere but a local Development database.
internal static class DevelopmentDatabaseGuard
{
    public static void Ensure(IHostEnvironment environment, NpgsqlDataSource dataSource, string toolName)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"The {toolName} is available only in the Development environment.");
        }

        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        var host = connection.Host?.Trim() ?? string.Empty;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(host, "::1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {toolName} may target only a loopback PostgreSQL host.");
        }

        if (string.IsNullOrWhiteSpace(connection.Database)
            || connection.Database.Contains("prod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {toolName} cannot target a production-like database name.");
        }
    }
}
