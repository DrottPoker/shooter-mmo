using System.Diagnostics.CodeAnalysis;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// The handler-facing API of one item transaction: its session identity and five facets.
// The kernel creates it after the transaction-local timeouts are set; it never commits
// or rolls back. Handlers use Connection and Transaction for their own statements.
internal sealed class ItemTransactionContext
{
    public ItemTransactionContext(
        ItemTransactionSession session,
        CharacterItemStateLocks characterStateLocks,
        ItemOperationsMetrics? operationsMetrics = null)
    {
        Session = session;
        Authority = new ItemActorAuthority(session.Actor);
        Locks = new ItemTransactionLocks(
            session,
            Authority,
            characterStateLocks,
            new ItemMutationScopeLocks(session, operationsMetrics));
        State = new ItemStateReader(session, Authority);
        Journal = new ItemChangeJournal(session, State);
        Placement = new ItemSlotPlacement(session);
    }

    public ItemTransactionSession Session { get; }

    public NpgsqlConnection Connection => Session.Connection;

    public NpgsqlTransaction Transaction => Session.Transaction;

    // The transaction handed to other modules' contracts.
    public PostgresTransactionScope Scope => Session.Scope;

    public Guid OperationId => Session.OperationId;

    public string OperationKind => Session.OperationKind;

    public ItemTransactionActor Actor => Session.Actor;

    public ItemActorAuthority Authority { get; }

    public ItemTransactionLocks Locks { get; }

    public ItemStateReader State { get; }

    public ItemChangeJournal Journal { get; }

    public ItemSlotPlacement Placement { get; }

    [DoesNotReturn]
    public static void Reject(string code, string message)
    {
        ItemTransactionRejection.Reject(code, message);
    }
}
