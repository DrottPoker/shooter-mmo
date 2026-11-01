using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// The complete mapping of database errors to stable item error codes. A null mapping
// means the error is unexpected and propagates.
public sealed class ItemTransactionOutcomesTests
{
    [Theory]
    [InlineData("57014", null, ItemTransactionErrorCodes.ItemTransactionTimeout, "The item transaction exceeded its configured database time limit.")]
    [InlineData("55P03", null, ItemTransactionErrorCodes.ItemTransactionTimeout, "The item transaction exceeded its configured database time limit.")]
    [InlineData("40P01", null, ItemTransactionErrorCodes.ItemTransactionTimeout, "The item transaction was aborted by concurrent database work. Retry it.")]
    [InlineData("40001", null, ItemTransactionErrorCodes.ItemTransactionTimeout, "The item transaction was aborted by concurrent database work. Retry it.")]
    [InlineData("23505", "ux_item_instances_container_slot", ItemTransactionErrorCodes.ItemSlotOccupied, "The requested destination slot is occupied.")]
    [InlineData("23505", "ux_item_instances_equipment_slot", ItemTransactionErrorCodes.EquipmentSlotOccupied, "The requested equipment slot is occupied.")]
    [InlineData("23505", "ux_recovery_deliveries_source", ItemTransactionErrorCodes.ItemOperationConflict, "The Recovery Storage source event was already applied.")]
    [InlineData("23514", "ck_item_instances_quantity", ItemTransactionErrorCodes.ItemQuantityChanged, "The requested item quantity is no longer valid.")]
    [InlineData("23505", "item_instances_pkey", ItemTransactionErrorCodes.ItemOperationConflict, "The item instance already exists.")]
    [InlineData("23503", "fk_item_instances_container_slot", ItemTransactionErrorCodes.ItemStateConflict, "The item state changed before the operation committed.")]
    [InlineData("23503", "fk_item_containers_bound_bag", ItemTransactionErrorCodes.ItemStateConflict, "The item state changed before the operation committed.")]
    [InlineData("23503", "fk_character_item_states_inventory_container", ItemTransactionErrorCodes.ItemStateConflict, "The item state changed before the operation committed.")]
    [InlineData("23503", "fk_character_item_states_bank_container", ItemTransactionErrorCodes.ItemStateConflict, "The item state changed before the operation committed.")]
    [InlineData("23503", "fk_character_item_states_recovery_container", ItemTransactionErrorCodes.ItemStateConflict, "The item state changed before the operation committed.")]
    public void MappedDatabaseErrorsBecomeStableRejections(
        string sqlState,
        string? constraintName,
        string expectedCode,
        string expectedMessage)
    {
        var mapped = ItemTransactionOutcomes.MapPostgresError(CreateException(sqlState, constraintName));

        Assert.NotNull(mapped);
        Assert.Equal(expectedCode, mapped.Code);
        Assert.Equal(expectedMessage, mapped.Message);
    }

    [Theory]
    [InlineData("23505", "item_operations_pkey")]
    [InlineData("23503", "item_instances_definition_id_fkey")]
    [InlineData("23514", "ck_item_containers_capacity")]
    [InlineData("42P01", null)]
    public void UnexpectedDatabaseErrorsAreNotMapped(string sqlState, string? constraintName)
    {
        Assert.Null(ItemTransactionOutcomes.MapPostgresError(CreateException(sqlState, constraintName)));
    }

    [Fact]
    public void RejectedResultCarriesOnlyTheError()
    {
        var operationId = Guid.NewGuid();

        var result = ItemTransactionOutcomes.CreateRejected(
            operationId,
            ItemOperationKinds.Grant,
            ItemTransactionErrorCodes.ItemNotFound,
            "Missing.");

        Assert.Equal(operationId, result.OperationId);
        Assert.Equal(ItemOperationKinds.Grant, result.OperationKind);
        Assert.False(result.Succeeded);
        Assert.Equal(new ItemTransactionError(ItemTransactionErrorCodes.ItemNotFound, "Missing."), result.Error);
        Assert.Empty(result.CharacterRevisions);
        Assert.Empty(result.ContainerRevisions);
        Assert.Empty(result.ItemRevisions);
        Assert.Empty(result.RecoveryDeliveryIds);
    }

    internal static PostgresException CreateException(string sqlState, string? constraintName)
    {
        return new PostgresException(
            "Test database error.",
            "ERROR",
            "ERROR",
            sqlState,
            constraintName: constraintName);
    }
}
