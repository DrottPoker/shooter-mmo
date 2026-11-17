using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Items.Audit;

// The economy audit's checks of what happened, as far as the retained history tells: item
// destructions, the operation journal, and the operations themselves. Retention trims all three
// after Retention:ItemHistory, so these checks look only at the rows still there and never need
// a row that retention removes.
internal static class ItemHistoryAuditChecks
{
    // Item ids are never reused, so an item that a record names as gone stays gone.
    public static SqlAuditCheck DestroyedButPresent { get; } = new(
        "item_destroyed_but_present",
        "No item instance that a destruction record names still exists.",
        AuditSql.Counted(
            "select count(*) from item_destructions",
            """
            select
                destruction.item_instance_id,
                destruction.definition_id,
                destruction.reason,
                destruction.source_operation_id,
                destruction.destroyed_at
            from item_destructions destruction
            join item_instances item on item.id = destruction.item_instance_id
            """,
            "item_instance_id"));

    // A journal row without a state after its change removed the item: a merge, a full split, a
    // sale, a consumption, an expiry, or a destruction.
    public static SqlAuditCheck JournalRemovedButPresent { get; } = new(
        "item_journal_removed_but_present",
        "No item instance whose removal the retained operation journal records still exists.",
        AuditSql.Counted(
            """
            select count(*)
            from item_operation_changes
            where item_instance_id is not null
              and after_state is null
            """,
            """
            select
                change.item_instance_id,
                change.operation_id,
                operation.operation_kind,
                change.change_kind
            from item_operation_changes change
            join item_operations operation on operation.operation_id = change.operation_id
            join item_instances item on item.id = change.item_instance_id
            where change.after_state is null
              and operation.status = 'committed'
            """,
            "item_instance_id, operation_id"));

    // For each retained committed operation of an Items kind, the change of every definition's
    // total: the quantity after the last change of each item less the quantity before its first,
    // with a created item counting from 0 and a removed one ending at 0. Arrays compare element by
    // element, so the max and min of [change index, quantity] pick the last and the first change
    // without an ordered aggregate, which leaves the planner free to group by hash. The quest and
    // economy commands of the modules above Items are not classified here, as
    // GAME_SERVICE_MODULES.md describes under Economy Audit.
    public static SqlAuditCheck JournalQuantityChanged { get; } = new(
        "item_journal_quantity_changed",
        "Every retained committed operation of an Items kind changed quantities only as its kind allows: a move keeps every definition's total, a creation never lowers one, and a removal never raises one.",
        AuditSql.Counted(
            """
            select count(*)
            from item_operations
            where status = 'committed'
              and (operation_kind = any(@MovingKinds)
                   or operation_kind = any(@CreatingKinds)
                   or operation_kind = any(@RemovingKinds))
            """,
            """
            with journal as (
                select
                    change.operation_id,
                    change.item_instance_id,
                    change.change_index,
                    coalesce(change.before_state ->> 'definitionId', change.after_state ->> 'definitionId') as definition_id,
                    coalesce((change.before_state ->> 'quantity')::bigint, 0) as before_quantity,
                    coalesce((change.after_state ->> 'quantity')::bigint, 0) as after_quantity
                from item_operation_changes change
                join item_operations operation on operation.operation_id = change.operation_id
                where change.item_instance_id is not null
                  and operation.status = 'committed'
                  and (operation.operation_kind = any(@MovingKinds)
                       or operation.operation_kind = any(@CreatingKinds)
                       or operation.operation_kind = any(@RemovingKinds))
            ),
            item_change as (
                select
                    operation_id,
                    min(definition_id) as definition_id,
                    (max(array[change_index::bigint, after_quantity]))[2]
                        - (min(array[change_index::bigint, before_quantity]))[2] as quantity_change
                from journal
                group by operation_id, item_instance_id
            ),
            definition_change as (
                select operation_id, definition_id, sum(quantity_change) as quantity_change
                from item_change
                group by operation_id, definition_id
            )
            select
                definition_change.operation_id,
                operation.operation_kind,
                definition_change.definition_id,
                definition_change.quantity_change
            from definition_change
            join item_operations operation on operation.operation_id = definition_change.operation_id
            where (operation.operation_kind = any(@MovingKinds) and definition_change.quantity_change <> 0)
               or (operation.operation_kind = any(@CreatingKinds) and definition_change.quantity_change < 0)
               or (operation.operation_kind = any(@RemovingKinds) and definition_change.quantity_change > 0)
            """,
            "operation_id, definition_id"),
        _ => new
        {
            MovingKinds = ItemOperationQuantityEffects.Moving.ToArray(),
            CreatingKinds = ItemOperationQuantityEffects.Creating.ToArray(),
            RemovingKinds = ItemOperationQuantityEffects.Removing.ToArray()
        });

    // The kernel claims and completes an operation in one transaction, so a pending row is never
    // committed; one that the audit sees for longer than the setting was left behind.
    public static SqlAuditCheck OperationPending { get; } = new(
        "item_operation_pending_too_long",
        "No item operation has been pending longer than Audit:PendingOperationSeconds.",
        AuditSql.Counted(
            "select count(*) from item_operations",
            """
            select
                operation_id,
                operation_kind,
                created_at
            from item_operations
            where status = 'pending'
              and created_at <= now() - make_interval(secs => @PendingOperationSeconds)
            """,
            "created_at, operation_id"),
        options => new { PendingOperationSeconds = options.PendingOperationAge.TotalSeconds });

    // A rejected or abandoned operation rolls its gold change back with everything else, so a
    // ledger row that an item operation wrote must belong to a committed operation, and the
    // operation acted for the character whose gold changed. The ledger keeps every row, but its
    // operations go after Retention:ItemHistory, so only the rows of retained operations are seen.
    public static SqlAuditCheck CurrencyOperationCommitted { get; } = new(
        "currency_change_operation_committed",
        "Every gold change that names a retained item operation belongs to a committed operation of the same character.",
        AuditSql.Counted(
            $"""
            select count(*)
            from {CharactersSql.CurrencyLedgerItemOperations} ledger
            join item_operations operation on operation.operation_id = ledger.item_operation_id
            """,
            $"""
            select
                ledger.id as ledger_row_id,
                ledger.character_id,
                ledger.item_operation_id,
                operation.operation_kind,
                operation.status,
                operation.actor_character_id
            from {CharactersSql.CurrencyLedgerItemOperations} ledger
            join item_operations operation on operation.operation_id = ledger.item_operation_id
            where operation.status <> 'committed'
               or operation.actor_character_id is distinct from ledger.character_id
            """,
            "ledger_row_id"));

    public static IReadOnlyList<IAuditCheck> All { get; } =
    [
        DestroyedButPresent,
        JournalRemovedButPresent,
        JournalQuantityChanged,
        OperationPending,
        CurrencyOperationCommitted
    ];
}
