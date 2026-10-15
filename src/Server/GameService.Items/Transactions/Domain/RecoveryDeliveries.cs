using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Recovery Storage delivery rules shared by the delivery, claim, expiry, Secure
// Container tier, and player death handlers.
internal static class RecoveryDeliveries
{
    public static IReadOnlyDictionary<Guid, long> ValidateDistinctItemExpectations(
        IReadOnlyList<ItemRevisionExpectation> expectations)
    {
        if (expectations is null || expectations.Count == 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "At least one item revision expectation is required.");
        }

        var grouped = expectations
            .GroupBy(expectation => expectation.ItemInstanceId)
            .ToArray();
        if (grouped.Any(group =>
                group.Key == Guid.Empty
                || group.Count() != 1
                || group.Single().Revision < 0))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "Item revision expectations must contain unique valid item ids and revisions.");
        }

        return grouped.ToDictionary(group => group.Key, group => group.Single().Revision);
    }

    public static void ValidateRecoverySource(string sourceKind, string sourceEventId)
    {
        if (string.IsNullOrWhiteSpace(sourceKind) || string.IsNullOrWhiteSpace(sourceEventId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "A Recovery Storage source kind and source event id are required.");
        }
    }

    public static void ValidateRecoveryTimeline(DateTime? availableAt, DateTime? expiresAt)
    {
        if (availableAt is not null && availableAt.Value.Kind != DateTimeKind.Utc)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "Recovery Storage availability time must use UTC.");
        }

        if (expiresAt is not null && expiresAt.Value.Kind != DateTimeKind.Utc)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "Recovery Storage expiry time must use UTC.");
        }

        if (availableAt is not null
            && expiresAt is not null
            && expiresAt.Value < availableAt.Value)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemOperationConflict,
                "Recovery Storage expiry cannot precede availability.");
        }
    }

    public static Task<int> GetNextRecoverySlotIndexAsync(
        ItemTransactionContext context,
        Guid recoveryContainerId,
        CancellationToken cancellationToken)
    {
        return context.Connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            select coalesce(max(slot_index) + 1, 0)
            from item_container_slots
            where container_id = @ContainerId;
            """,
            new { ContainerId = recoveryContainerId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    public static async Task<RecoveryDeliveryState?> LoadRecoveryDeliveryAsync(
        ItemTransactionContext context,
        Guid deliveryId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var deliveryLock = forUpdate ? "for update" : string.Empty;
        var linkLock = forUpdate ? "for update of link" : string.Empty;
        using var grid = await context.Connection.QueryMultipleAsync(new CommandDefinition(
            $"""
            select
                id as "DeliveryId",
                character_id as "CharacterId",
                recovery_storage_container_id as "RecoveryStorageContainerId",
                revision as "Revision",
                claimed_at as "ClaimedAt",
                (available_at is null or available_at <= now()) as "IsAvailable",
                (expires_at is not null and expires_at <= now()) as "IsExpired"
            from recovery_deliveries
            where id = @DeliveryId
            {deliveryLock};

            select
                link.item_instance_id as "ItemInstanceId",
                link.item_order as "ItemOrder",
                item.container_slot_index as "ContainerSlotIndex"
            from recovery_delivery_items link
            join item_instances item on item.id = link.item_instance_id
            where link.recovery_delivery_id = @DeliveryId
            order by link.item_order, link.item_instance_id
            {linkLock};
            """,
            new { DeliveryId = deliveryId },
            context.Transaction,
            cancellationToken: cancellationToken));
        var row = await grid.ReadSingleOrDefaultAsync<RecoveryDeliveryRow>();
        var items = (await grid.ReadAsync<RecoveryDeliveryItemRow>()).ToArray();
        return row is null
            ? null
            : new RecoveryDeliveryState(
                row.DeliveryId,
                row.CharacterId,
                row.RecoveryStorageContainerId,
                row.Revision,
                row.ClaimedAt,
                row.IsAvailable,
                row.IsExpired,
                items);
    }

    private sealed class RecoveryDeliveryRow
    {
        public Guid DeliveryId { get; set; }

        public Guid CharacterId { get; set; }

        public Guid RecoveryStorageContainerId { get; set; }

        public long Revision { get; set; }

        public DateTime? ClaimedAt { get; set; }

        public bool IsAvailable { get; set; }

        public bool IsExpired { get; set; }
    }
}

internal sealed class RecoveryDeliveryItemRow
{
    public Guid ItemInstanceId { get; set; }

    public int ItemOrder { get; set; }

    public int ContainerSlotIndex { get; set; }
}

internal sealed record RecoveryDeliveryState(
    Guid DeliveryId,
    Guid CharacterId,
    Guid RecoveryStorageContainerId,
    long Revision,
    DateTime? ClaimedAt,
    bool IsAvailable,
    bool IsExpired,
    IReadOnlyList<RecoveryDeliveryItemRow> Items);
