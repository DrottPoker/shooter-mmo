using Dapper;
using Npgsql;

namespace Mmorpg.GameService.Platform.Database;

// Reads of the PostgreSQL system catalog that a plan needs before the migrations ran.
public static class PostgresCatalog
{
    // Whether the table exists on the connection's search path. The name is a fixed table
    // name of the caller, never input.
    public static Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        return connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select to_regclass(@Table) is not null;",
            new { Table = table },
            cancellationToken: cancellationToken));
    }
}
