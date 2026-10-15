using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Destroys the remaining items of a corpse at its absolute expiry and closes it.
internal sealed class CorpseExpiryCommandHandler
    : IItemCommandHandler<ExpireCorpseCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ExpireCorpseCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSystemAuthority();
        if (command.CorpseId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotFound,
                "A corpse id is required.");
        }

        var corpse = await context.Connection.QuerySingleOrDefaultAsync<ExpiringCorpseRow>(
            new CommandDefinition(
                """
                select
                    id as "CorpseId",
                    expires_at as "ExpiresAt",
                    closed_at as "ClosedAt",
                    close_reason as "CloseReason",
                    expiry_operation_id as "ExpiryOperationId"
                from corpses
                where id = @CorpseId
                for update;
                """,
                new { command.CorpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (corpse is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotFound,
                "The corpse was not found.");
        }

        if (corpse.ExpiryOperationId != context.OperationId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseStateChanged,
                "Corpse expiry must use the durable expiry operation id.");
        }

        if (corpse.ClosedAt is not null)
        {
            if (!string.Equals(corpse.CloseReason, "expired", StringComparison.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.CorpseStateChanged,
                    "The corpse was closed by another lifecycle operation.");
            }

            context.Journal.AddMetadataAudit(
                "corpse_expiry_replayed",
                new { command.CorpseId, corpse.ClosedAt },
                new { command.CorpseId, corpse.ClosedAt });
            return;
        }

        var databaseTime = await context.Connection.QuerySingleAsync<DateTime>(
            new CommandDefinition(
                "select now();",
                transaction: context.Transaction,
                cancellationToken: cancellationToken));
        if (corpse.ExpiresAt > databaseTime)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.CorpseNotExpired,
                "The corpse has not reached its absolute expiry time.");
        }

        var sectionContainerIds = (await context.Connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                select container_id
                from corpse_sections
                where corpse_id = @CorpseId
                order by container_id;
                """,
                new { command.CorpseId },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        var directItemIds = sectionContainerIds.Length == 0
            ? []
            : (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from item_instances
                where container_id = any(@ContainerIds)
                order by id;
                """,
                new { ContainerIds = sectionContainerIds },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        await context.Locks.LockMutationScopeAsync(
            directItemIds,
            sectionContainerIds,
            cancellationToken);

        var expiringItems = directItemIds.Length == 0
            ? []
            : (await context.Connection.QueryAsync<ExpiringCorpseItemRow>(new CommandDefinition(
                """
                select distinct
                    item.id as "ItemInstanceId",
                    item.definition_id as "DefinitionId",
                    item.quantity as "Quantity",
                    (bag.definition_id is not null) as "IsBag"
                from item_instances item
                left join bag_definitions bag on bag.definition_id = item.definition_id
                where item.container_id = any(@ContainerIds)
                   or item.container_id in (
                        select bag_container.id
                        from item_instances root
                        join item_containers bag_container
                          on bag_container.bound_bag_item_instance_id = root.id
                         and bag_container.container_type = 'bag_contents'
                        where root.id = any(@DirectItemIds))
                order by item.id;
                """,
                new
                {
                    ContainerIds = sectionContainerIds,
                    DirectItemIds = directItemIds
                },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();

        foreach (var item in expiringItems)
        {
            var beforeState = await context.Journal.CaptureItemStateJsonAsync(
                item.ItemInstanceId,
                cancellationToken);
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
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
                    'corpse_expired');
                """,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    OperationId = context.OperationId,
                    item.Quantity
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            context.Journal.AddItemAudit(
                "corpse_item_expired",
                item.ItemInstanceId,
                beforeState,
                null);
        }

        var ordinaryItemIds = expiringItems
            .Where(item => !item.IsBag)
            .Select(item => item.ItemInstanceId)
            .ToArray();
        var bagItemIds = expiringItems
            .Where(item => item.IsBag)
            .Select(item => item.ItemInstanceId)
            .ToArray();
        await DeleteItemsAsync(context, ordinaryItemIds, cancellationToken);
        await DeleteItemsAsync(context, bagItemIds, cancellationToken);

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_containers
            set lifecycle = 'closed',
                revision = revision + 1,
                updated_at = now()
            where id in (
                select container_id
                from corpse_sections
                where corpse_id = @CorpseId);

            update corpses
            set revision = revision + 1,
                closed_at = now(),
                close_reason = 'expired'
            where id = @CorpseId
              and closed_at is null;
            """,
            new { command.CorpseId },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.Journal.AddMetadataAudit(
            "corpse_expired",
            new
            {
                command.CorpseId,
                corpse.ExpiresAt,
                RemainingItemCount = expiringItems.Length
            },
            new
            {
                command.CorpseId,
                ClosedAt = databaseTime,
                DestroyedItemCount = expiringItems.Length
            });
    }

    private static async Task DeleteItemsAsync(
        ItemTransactionContext context,
        Guid[] itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Length == 0)
        {
            return;
        }

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            delete from item_instances
            where id = any(@ItemIds);
            """,
            new { ItemIds = itemIds },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class ExpiringCorpseRow
    {
        public Guid CorpseId { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string? CloseReason { get; set; }

        public Guid ExpiryOperationId { get; set; }
    }

    private sealed class ExpiringCorpseItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public string DefinitionId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public bool IsBag { get; set; }
    }
}
