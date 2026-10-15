using Dapper;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Removes every protected item of an abandoned quest grant.
internal sealed class QuestItemCommandHandler
    : IItemCommandHandler<AbandonQuestItemsCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AbandonQuestItemsCommand command,
        CancellationToken cancellationToken)
    {
        if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker)
        {
            context.Authority.EnsureQuestNpcAccess();
        }
        else
        {
            context.Authority.EnsureSystemAuthority();
        }
        if (string.IsNullOrWhiteSpace(command.QuestGrantId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestGrantInvalid,
                "A quest grant id is required.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var questGrantId = command.QuestGrantId.Trim();
        var itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            QuestGrantItemIdsSql,
            new
            {
                command.CharacterId,
                QuestGrantId = questGrantId,
                SourceKind = ItemPolicySourceKinds.QuestGrant
            },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();
        await context.LockMutationScopeAsync(itemIds, [], cancellationToken);
        itemIds = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
            QuestGrantItemIdsSql,
            new
            {
                command.CharacterId,
                QuestGrantId = questGrantId,
                SourceKind = ItemPolicySourceKinds.QuestGrant
            },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();

        if (itemIds.Length == 0)
        {
            var unchanged = new { QuestGrantId = questGrantId, RemovedItemCount = 0 };
            context.AddMetadataAudit("quest_items_already_absent", unchanged, unchanged);
            return;
        }

        foreach (var itemInstanceId in itemIds.Order())
        {
            var item = await context.State.LoadItemAsync(itemInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Quest item '{itemInstanceId}' disappeared while its owner was locked.");
            var beforeState = await context.CaptureItemStateJsonAsync(
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
                    'quest_abandoned');

                delete from item_instances
                where id = @ItemInstanceId;
                """,
                new
                {
                    item.ItemInstanceId,
                    item.DefinitionId,
                    context.OperationId,
                    item.Quantity
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
            {
                // Recovery items own their slot row, and a delivery that loses its last
                // item can never be claimed, so both are removed with the item.
                await context.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    delete from item_container_slots
                    where container_id = @ContainerId
                      and slot_index = @SlotIndex;

                    delete from recovery_deliveries delivery
                    where delivery.recovery_storage_container_id = @ContainerId
                      and delivery.claimed_at is null
                      and not exists (
                          select 1
                          from recovery_delivery_items link
                          where link.recovery_delivery_id = delivery.id);
                    """,
                    new
                    {
                        item.ContainerId,
                        SlotIndex = item.ContainerSlotIndex
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            }

            context.AddItemAudit("quest_item_removed", item.ItemInstanceId, beforeState, null);
            context.TouchContainer(item.ContainerId);
        }

        context.TouchCharacter(command.CharacterId);
    }

    private const string QuestGrantItemIdsSql =
        """
        select item.id
        from item_instances item
        join item_definitions definition
          on definition.id = item.definition_id
         and definition.category_id = 'quest_item'
        left join item_containers container on container.id = item.container_id
        left join item_instances source_bag
          on source_bag.id = container.bound_bag_item_instance_id
        join item_instance_policies policy
          on policy.item_instance_id = item.id
         and policy.policy_kind = 'protected_on_death'
         and policy.status = 'active'
         and policy.source_kind = @SourceKind
        where coalesce(
                item.equipped_character_id,
                container.owner_character_id,
                source_bag.equipped_character_id) = @CharacterId
          and policy.source_id = @QuestGrantId
        order by item.id;
        """;
}
