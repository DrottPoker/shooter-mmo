using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Kernel-owned finalization of a successful handler: container revisions, carry
// weight and the hard cap, character revisions, result item revisions, and audit rows,
// in this order. It fails a transaction whose handler recorded no audit change.
internal sealed class ItemTransactionFinalizer(
    ItemTransactionSession session,
    ItemChangeJournal journal)
{
    // Bounds the stored result; no realtime change message carries more items anyway.
    private const int MaximumReportedItemChanges = 32;

    private readonly CarryStateCalculator carryStates = new(session);

    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction;

    private Guid OperationId => session.OperationId;

    private string OperationKind => session.OperationKind;

    public async Task<ItemTransactionResult> FinalizeAsync(
        CancellationToken cancellationToken)
    {
        if (journal.AuditChanges.Count == 0)
        {
            throw new InvalidOperationException(
                $"Item operation '{OperationKind}' produced no audit changes.");
        }

        var containerIds = journal.TouchedContainerIds.Order().ToArray();
        if (containerIds.Length > 0)
        {
            await Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_containers
                set revision = revision + 1,
                    updated_at = now()
                where id = any(@ContainerIds);
                """,
                new { ContainerIds = containerIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var containerResults = containerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<ItemTransactionContainerRevision>(
                new CommandDefinition(
                    """
                    select
                        id as "ContainerId",
                        revision as "Revision"
                    from item_containers
                    where id = any(@ContainerIds)
                    order by id;
                    """,
                    new { ContainerIds = containerIds },
                    Transaction,
                    cancellationToken: cancellationToken))).ToArray();

        var characterResults = new List<ItemTransactionCharacterRevision>();
        foreach (var characterId in journal.TouchedCharacterIds.Order())
        {
            var carry = await carryStates.CalculateCarryStateAsync(characterId, cancellationToken);
            var previousCarry = await Connection.QuerySingleAsync<CharacterCarryResult>(
                new CommandDefinition(
                    """
                    select
                        revision as "Revision",
                        carried_weight as "CarriedWeight",
                        carry_capacity as "CarryCapacity"
                    from character_item_states
                    where character_id = @CharacterId;
                    """,
                    new { CharacterId = characterId },
                    Transaction,
                    cancellationToken: cancellationToken));
            if (!journal.AllowsInvoluntaryHardCapOverflow
                && !EncumbranceRules.IsAllowedVoluntaryTransition(
                    previousCarry.CarriedWeight,
                    previousCarry.CarryCapacity,
                    carry.CarriedWeight,
                    carry.CarryCapacity))
            {
                Reject(
                    ItemTransactionErrorCodes.CarryWeightLimitExceeded,
                    "The operation would exceed the character carry-weight hard cap.");
            }

            var state = await Connection.QuerySingleAsync<CharacterCarryResult>(
                new CommandDefinition(
                    $"""
                    update character_item_states
                    set revision = revision + 1,
                        carried_weight = @CarriedWeight,
                        carry_capacity = @CarryCapacity,
                        updated_at = now()
                    where character_id = @CharacterId
                    returning
                        revision as "Revision",
                        carried_weight as "CarriedWeight",
                        carry_capacity as "CarryCapacity",
                        {CharactersSql.CurrencyOf("character_item_states.character_id")} as "Currency";
                    """,
                    new
                    {
                        CharacterId = characterId,
                        carry.CarriedWeight,
                        carry.CarryCapacity
                    },
                    Transaction,
                    cancellationToken: cancellationToken));
            characterResults.Add(new ItemTransactionCharacterRevision(
                characterId,
                state.Revision,
                state.CarriedWeight,
                state.CarryCapacity,
                state.Currency));
        }

        var itemChanges = characterResults.Count == 0
            ? null
            : await LoadItemChangesAsync(cancellationToken);

        var resultItems = journal.ResultItemIds.Count == 0
            ? []
            : (await Connection.QueryAsync<ItemTransactionItemRevision>(new CommandDefinition(
                """
                select
                    id as "ItemInstanceId",
                    revision as "Revision"
                from item_instances
                where id = any(@ItemIds)
                order by id;
                """,
                new { ItemIds = journal.ResultItemIds.Order().ToArray() },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        for (var index = 0; index < journal.AuditChanges.Count; index++)
        {
            var change = journal.AuditChanges[index];
            await Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_operation_changes (
                    operation_id,
                    change_index,
                    item_instance_id,
                    change_kind,
                    before_state,
                    after_state)
                values (
                    @OperationId,
                    @ChangeIndex,
                    @ItemInstanceId,
                    @ChangeKind,
                    cast(@BeforeState as jsonb),
                    cast(@AfterState as jsonb));
                """,
                new
                {
                    OperationId,
                    ChangeIndex = index,
                    change.ItemInstanceId,
                    change.ChangeKind,
                    change.BeforeState,
                    change.AfterState
                },
                Transaction,
                cancellationToken: cancellationToken));
        }

        return new ItemTransactionResult(
            OperationId,
            OperationKind,
            true,
            null,
            characterResults,
            containerResults,
            resultItems,
            journal.RecoveryDeliveryIds.Order().ToArray(),
            itemChanges);
    }

    // The final state of every changed item, so the simulation client can patch its
    // inventory, and of every item inside a changed Bag that sits in the Bag slot, whose
    // contents the client only sees once the Bag is equipped. A transaction above the limit
    // reports none, and the client refreshes.
    private async Task<IReadOnlyList<ItemTransactionItemChange>?> LoadItemChangesAsync(
        CancellationToken cancellationToken)
    {
        if (journal.ChangedItemIds.Count > MaximumReportedItemChanges)
        {
            return null;
        }

        var changedIds = journal.ChangedItemIds.Order().ToArray();
        if (changedIds.Length == 0)
        {
            return [];
        }

        using var grid = await Connection.QueryMultipleAsync(new CommandDefinition(
            """
            with reported as (
                select id
                from item_instances
                where id = any(@ItemIds)
                union
                select contents.id
                from item_instances bag
                join item_containers container
                  on container.bound_bag_item_instance_id = bag.id
                 and container.container_type = 'bag_contents'
                join item_instances contents on contents.container_id = container.id
                where bag.id = any(@ItemIds)
                  and bag.equipment_slot_id = @BagSlot
            )
            select
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity",
                item.revision as "Revision",
                item.container_id as "ContainerId",
                item.container_slot_index as "ContainerSlotIndex",
                item.equipped_character_id as "EquippedCharacterId",
                item.equipment_slot_id as "EquipmentSlotId",
                contents.id as "BagContentsContainerId",
                contents.revision as "BagContentsRevision"
            from item_instances item
            join reported on reported.id = item.id
            left join item_containers contents
              on contents.bound_bag_item_instance_id = item.id
             and contents.container_type = 'bag_contents'
             and item.equipment_slot_id = @BagSlot;

            select
                policy.item_instance_id as "ItemInstanceId",
                policy.policy_kind as "PolicyKind",
                policy.status as "Status",
                policy.source_kind as "SourceKind"
            from item_instance_policies policy
            join item_instances item on item.id = policy.item_instance_id
            where policy.status = 'active'
              and (item.id = any(@ItemIds)
                or item.container_id in (
                    select container.id
                    from item_instances bag
                    join item_containers container
                      on container.bound_bag_item_instance_id = bag.id
                     and container.container_type = 'bag_contents'
                    where bag.id = any(@ItemIds)
                      and bag.equipment_slot_id = @BagSlot))
            order by policy.item_instance_id, policy.policy_kind;
            """,
            new { ItemIds = changedIds, BagSlot = ItemEquipmentSlotIds.Bag },
            Transaction,
            cancellationToken: cancellationToken));
        var items = (await grid.ReadAsync<ChangedItemRow>()).ToDictionary(row => row.ItemInstanceId);
        var policies = (await grid.ReadAsync<ChangedItemPolicyRow>()).ToLookup(row => row.ItemInstanceId);
        var itemIds = changedIds.Union(items.Keys).Order().ToArray();
        if (itemIds.Length > MaximumReportedItemChanges)
        {
            return null;
        }

        return itemIds
            .Select(itemId => items.TryGetValue(itemId, out var item)
                ? new ItemTransactionItemChange(
                    itemId,
                    false,
                    item.DefinitionId,
                    item.Quantity,
                    item.Revision,
                    item.ContainerId,
                    item.ContainerSlotIndex,
                    item.EquippedCharacterId,
                    item.EquipmentSlotId,
                    policies[itemId]
                        .Select(policy => new ItemPolicySummaryResponse(
                            policy.PolicyKind,
                            policy.Status,
                            ItemPolicySourceKinds.ToProtectionSource(policy.SourceKind)))
                        .ToArray(),
                    item.BagContentsContainerId,
                    item.BagContentsRevision)
                : new ItemTransactionItemChange(itemId, true, null, 0, 0, null, null, null, null, []))
            .ToArray();
    }
    private sealed class CharacterCarryResult
    {
        public long Revision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }

        public long Currency { get; set; }
    }

    private sealed class ChangedItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public string DefinitionId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public long Revision { get; set; }

        public Guid? ContainerId { get; set; }

        public int? ContainerSlotIndex { get; set; }

        public Guid? EquippedCharacterId { get; set; }

        public string? EquipmentSlotId { get; set; }

        public Guid? BagContentsContainerId { get; set; }

        public long? BagContentsRevision { get; set; }
    }

    private sealed class ChangedItemPolicyRow
    {
        public Guid ItemInstanceId { get; set; }

        public string PolicyKind { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string SourceKind { get; set; } = string.Empty;
    }
}
