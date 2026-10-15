using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// The identity of one item transaction: its connection and transaction, operation, and
// actor. Scope hands the same transaction to other modules' contracts.
internal sealed class ItemTransactionSession(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid operationId,
    string operationKind,
    ItemTransactionActor actor)
{
    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public PostgresTransactionScope Scope { get; } = new(connection, transaction);

    public Guid OperationId { get; } = operationId;

    public string OperationKind { get; } = operationKind;

    public ItemTransactionActor Actor { get; } = actor;
}
