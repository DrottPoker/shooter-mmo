using Npgsql;

namespace Mmorpg.GameService.Platform.Database;

// The caller's open connection and transaction. A module contract that receives a
// scope runs its SQL in that transaction and never commits, rolls back, or opens its
// own connection, so the caller keeps one atomic unit with its snapshot and timeouts.
public sealed class PostgresTransactionScope
{
    public PostgresTransactionScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }
}
