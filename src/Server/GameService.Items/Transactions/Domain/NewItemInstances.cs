using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Creates one new item instance in an empty container slot with the definition's default
// policies and, for a Bag, its closed contents container. Every path that creates items
// from nothing uses it, so a new Bag always gets its contents container.
internal static class NewItemInstances
{
    // Adds the writes of the new item to the caller's batch. An ordinary item leaves them there
    // unsent, so they travel with what the caller adds next, such as the read of the new item's
    // audit state. A Bag sends them with the read of its slot count and then creates its
    // contents container, which needs that count.
    public static async Task CreateAsync(
        ItemTransactionContext context,
        PostgresBatch write,
        Guid itemInstanceId,
        ResolvedItemDefinition definition,
        int quantity,
        Guid containerId,
        int slotIndex,
        string policySourceKind,
        string policySourceId,
        CancellationToken cancellationToken)
    {
        write.Execute(
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
                Quantity = quantity,
                ContainerId = containerId,
                SlotIndex = slotIndex
            });

        foreach (var policyKind in definition.RuntimeDefinition.DefaultPolicies)
        {
            write.Execute(
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
                });
        }

        if (!definition.IsBag)
        {
            return;
        }

        var bagContainerId = Guid.NewGuid();
        var slots = write.QuerySingleOrDefault<int>(
            """
            select count(*)
            from bag_definition_slots
            where definition_id = @DefinitionId;
            """,
            new { DefinitionId = definition.RuntimeDefinition.Id });
        await write.ExecuteAsync(cancellationToken);
        var slotCapacity = slots.Value;
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
}
