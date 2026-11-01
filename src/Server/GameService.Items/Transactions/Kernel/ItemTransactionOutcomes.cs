using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// Rejected results and the mapping of database errors to stable item error codes.
internal static class ItemTransactionOutcomes
{
    public static ItemTransactionResult CreateRejected(
        Guid operationId,
        string operationKind,
        string errorCode,
        string errorMessage)
    {
        return new ItemTransactionResult(
            operationId,
            operationKind,
            false,
            new ItemTransactionError(errorCode, errorMessage),
            [],
            [],
            [],
            []);
    }

    public static ItemTransactionError? MapPostgresError(PostgresException exception)
    {
        if (exception.SqlState is "57014" or "55P03")
        {
            return new ItemTransactionError(
                ItemTransactionErrorCodes.ItemTransactionTimeout,
                "The item transaction exceeded its configured database time limit.");
        }

        if (exception.SqlState is "40P01" or "40001")
        {
            return new ItemTransactionError(
                ItemTransactionErrorCodes.ItemTransactionTimeout,
                "The item transaction was aborted by concurrent database work. Retry it.");
        }

        return exception.ConstraintName switch
        {
            "ux_item_instances_container_slot" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemSlotOccupied,
                "The requested destination slot is occupied."),
            "ux_item_instances_equipment_slot" => new ItemTransactionError(
                ItemTransactionErrorCodes.EquipmentSlotOccupied,
                "The requested equipment slot is occupied."),
            "ux_recovery_deliveries_source" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The Recovery Storage source event was already applied."),
            "ck_item_instances_quantity" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "The requested item quantity is no longer valid."),
            // Derived item ids, such as Mob loot ids, collide only with an existing item.
            "item_instances_pkey" => new ItemTransactionError(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The item instance already exists."),
            // Deferred foreign keys fail at COMMIT when a referenced slot, container, or
            // Bag changed concurrently.
            "fk_item_instances_container_slot"
                or "fk_item_containers_bound_bag"
                or "fk_character_item_states_inventory_container"
                or "fk_character_item_states_bank_container"
                or "fk_character_item_states_recovery_container" => new ItemTransactionError(
                    ItemTransactionErrorCodes.ItemStateConflict,
                    "The item state changed before the operation committed."),
            _ => null
        };
    }
}
