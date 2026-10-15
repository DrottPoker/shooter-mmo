using Dapper;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Grants a new item, including the idempotent protected quest item grant.
internal sealed class ItemGrantCommandHandler
    : IItemCommandHandler<GrantItemCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        GrantItemCommand command,
        CancellationToken cancellationToken)
    {
        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [],
            [command.DestinationContainerId],
            cancellationToken);

        var definition = await context.LoadDefinitionAsync(command.DefinitionId, cancellationToken);
        if (command.Quantity <= 0
            || command.Quantity > definition.RuntimeDefinition.MaximumStackSize)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStackLimitExceeded,
                "The grant quantity is outside the definition stack limit.");
        }

        var hasPolicySourceKind = !string.IsNullOrWhiteSpace(command.PolicySourceKind);
        var hasPolicySourceId = !string.IsNullOrWhiteSpace(command.PolicySourceId);
        if (hasPolicySourceKind != hasPolicySourceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestGrantInvalid,
                "An explicit policy source requires both source kind and source id.");
        }

        var policySourceKind = hasPolicySourceKind
            ? command.PolicySourceKind!.Trim()
            : ItemPolicySourceKinds.CatalogDefault;
        var policySourceId = hasPolicySourceId
            ? command.PolicySourceId!.Trim()
            : definition.RuntimeDefinition.Id;
        if (hasPolicySourceKind)
        {
            if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker)
            {
                context.EnsureQuestNpcAccess();
            }
            else
            {
                context.EnsureSystemAuthority();
            }

            if (!string.Equals(
                    policySourceKind,
                    ItemPolicySourceKinds.QuestGrant,
                    StringComparison.Ordinal)
                || !string.Equals(
                    definition.RuntimeDefinition.Category,
                    ItemCategoryIds.QuestItem,
                    StringComparison.Ordinal)
                || !definition.RuntimeDefinition.DefaultPolicies.Contains(
                    ItemPolicyIds.ProtectedOnDeath,
                    StringComparer.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestGrantInvalid,
                    "Explicit grant lineage is limited to protected quest item grants.");
            }

            var existingQuestItemId = await context.Connection.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    select item.id
                    from item_instances item
                    left join item_containers container on container.id = item.container_id
                    left join item_instances source_bag
                      on source_bag.id = container.bound_bag_item_instance_id
                    join item_instance_policies policy
                      on policy.item_instance_id = item.id
                     and policy.policy_kind = 'protected_on_death'
                     and policy.status = 'active'
                    where coalesce(
                            item.equipped_character_id,
                            container.owner_character_id,
                            source_bag.equipped_character_id) = @CharacterId
                      and item.definition_id = @DefinitionId
                      and policy.source_kind = @SourceKind
                      and policy.source_id = @SourceId
                    order by item.id
                    limit 1;
                    """,
                    new
                    {
                        command.CharacterId,
                        DefinitionId = definition.RuntimeDefinition.Id,
                        SourceKind = policySourceKind,
                        SourceId = policySourceId
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            if (existingQuestItemId is not null)
            {
                context.AddMetadataAudit(
                    "quest_grant_already_satisfied",
                    new { QuestGrantId = policySourceId, ItemInstanceId = existingQuestItemId },
                    new { QuestGrantId = policySourceId, ItemInstanceId = existingQuestItemId });
                context.IncludeResultItem(existingQuestItemId.Value);
                return;
            }
        }

        var destination = await context.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var slotIndex = await context.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var itemInstanceId = Guid.NewGuid();
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @Quantity,
                @ContainerId,
                @SlotIndex);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = definition.RuntimeDefinition.Id,
                command.Quantity,
                ContainerId = destination.ContainerId,
                SlotIndex = slotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        foreach (var policyKind in definition.RuntimeDefinition.DefaultPolicies)
        {
            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_instance_policies (
                    id,
                    item_instance_id,
                    policy_kind,
                    source_kind,
                    source_id)
                values (
                    @PolicyId,
                    @ItemInstanceId,
                    @PolicyKind,
                    @SourceKind,
                    @SourceId);
                """,
                new
                {
                    PolicyId = Guid.NewGuid(),
                    ItemInstanceId = itemInstanceId,
                    PolicyKind = policyKind,
                    SourceKind = policySourceKind,
                    SourceId = policySourceId
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        if (definition.IsBag)
        {
            var bagContainerId = Guid.NewGuid();
            var slotCapacity = await context.Connection.QuerySingleAsync<int>(new CommandDefinition(
                """
                select count(*)
                from bag_definition_slots
                where definition_id = @DefinitionId;
                """,
                new { DefinitionId = definition.RuntimeDefinition.Id },
                context.Transaction,
                cancellationToken: cancellationToken));
            if (slotCapacity <= 0)
            {
                throw new InvalidOperationException(
                    $"Bag definition '{definition.RuntimeDefinition.Id}' has no slots.");
            }

            await context.Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_containers (
                    id,
                    container_type,
                    bound_bag_item_instance_id,
                    slot_capacity,
                    lifecycle)
                values (
                    @ContainerId,
                    'bag_contents',
                    @ItemInstanceId,
                    @SlotCapacity,
                    'closed');

                insert into item_container_slots (container_id, slot_index, slot_kind)
                select @ContainerId, slot_index, slot_kind
                from bag_definition_slots
                where definition_id = @DefinitionId
                order by slot_index;

                insert into item_container_slot_tags (container_id, slot_index, tag_id)
                select @ContainerId, slot_index, tag_id
                from bag_definition_slot_tags
                where definition_id = @DefinitionId
                order by slot_index, tag_id;
                """,
                new
                {
                    ContainerId = bagContainerId,
                    ItemInstanceId = itemInstanceId,
                    SlotCapacity = slotCapacity,
                    DefinitionId = definition.RuntimeDefinition.Id
                },
                context.Transaction,
                cancellationToken: cancellationToken));
        }

        var afterState = await context.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.AddItemAudit("item_granted", itemInstanceId, null, afterState);
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(itemInstanceId);
    }
}
