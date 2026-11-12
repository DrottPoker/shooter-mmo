using System.Collections.Concurrent;
using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Deletes a whole locked item and records why in item_destructions, in one statement batch.
// The reason is part of the statement text, so it must be a lowercase identifier.
internal static class ItemDestructions
{
    private static readonly ConcurrentDictionary<string, string> StatementsByReason = new(StringComparer.Ordinal);

    public static Task DestroyAsync(
        ItemTransactionContext context,
        LockedItem item,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureReason(reason);
        return context.Connection.ExecuteAsync(new CommandDefinition(
            StatementsByReason.GetOrAdd(reason, Statement),
            new
            {
                item.ItemInstanceId,
                item.DefinitionId,
                context.OperationId,
                item.Quantity
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    public static void EnsureReason(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (!reason.All(character => character is (>= 'a' and <= 'z') or '_')
            || reason[0] == '_')
        {
            throw new ArgumentException(
                $"The destruction reason '{reason}' is not a lowercase identifier.",
                nameof(reason));
        }
    }

    private static string Statement(string reason)
    {
        return $"""
            insert into item_destructions (
                item_instance_id,
                definition_id,
                source_operation_id,
                quantity,
                reason)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @OperationId,
                @Quantity,
                '{reason}');

            delete from item_instances
            where id = @ItemInstanceId;
            """;
    }
}
