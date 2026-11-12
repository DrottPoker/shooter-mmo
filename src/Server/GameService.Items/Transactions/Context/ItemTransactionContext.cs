using System.Diagnostics.CodeAnalysis;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Contracts;

// The handler-facing API of one item transaction: its session identity and five facets.
// The kernel creates it after the transaction-local timeouts are set; it never commits
// or rolls back. Handlers in Items use Connection and Transaction for their own statements;
// commands above Items change items only through the facets and the item primitives.
public sealed class ItemTransactionContext
{
    internal ItemTransactionContext(
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

    internal ItemTransactionSession Session { get; }

    internal NpgsqlConnection Connection => Session.Connection;

    internal NpgsqlTransaction Transaction => Session.Transaction;

    // The transaction handed to other modules' contracts.
    public PostgresTransactionScope Scope => Session.Scope;

    public Guid OperationId => Session.OperationId;

    internal string OperationKind => Session.OperationKind;

    // The actor whose authority the kernel checked, such as the Shard and worker runtime of a
    // live operation, which a command's audit names.
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
