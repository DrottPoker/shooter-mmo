using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Loots everything that fits from a durable corpse in one transaction. CorpseLootAllPlanner
// plans the steps from the locked corpse and carry state, and each step commits under the
// same placement rules as the single loot it stands for.
internal sealed class CorpseLootAllCommandHandler : IItemCommandHandler<LootAllCorpseItemsCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        LootAllCorpseItemsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CharacterId == Guid.Empty
            || command.CorpseId == Guid.Empty
            || command.ExpectedCorpseRevision < 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationOperationInvalid,
                "The corpse Loot all command is incomplete or invalid.");
        }

        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(context, command.CorpseId, cancellationToken);
        if (corpse.Revision != command.ExpectedCorpseRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The corpse changed before Loot all committed.");
        }

        var state = new LootAllState(context);
        var looter = await state.LoadLooterAsync(character, cancellationToken);
        var corpseItems = await LoadCorpseItemsAsync(context, state, command.CorpseId, cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            corpseItems.Select(item => item.Item.ItemInstanceId).Concat(looter.ItemInstanceIds),
            corpseItems.Select(item => item.SectionContainerId)
                .Append(character.PermanentInventoryContainerId)
                .Concat(looter.BagContentsContainerId is Guid bagId ? new[] { bagId } : Array.Empty<Guid>()),
            cancellationToken);

        var plan = CorpseLootAllPlanner.Plan(new LootAllRequest(
            state.FindDefinition,
            state.MeetsLevelRequirement,
            character.CarriedWeight,
            character.CarryCapacity,
            looter.Equipment,
            looter.PermanentInventory,
            looter.BagContents,
            corpseItems.Select(item => item.Item),
            false));
        if (plan.Steps.Count == 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.InventoryFull,
                corpseItems.Count == 0
                    ? "The corpse is empty."
                    : "Nothing in the corpse fits in your equipment or inventory.");
        }

        var sectionContainers = corpseItems
            .ToDictionary(item => item.Item.ItemInstanceId, item => item.SectionContainerId);
        foreach (var step in plan.Steps)
        {
            await ExecuteStepAsync(context, state, command, step, cancellationToken);
            context.Journal.TouchContainer(sectionContainers[step.Item.ItemInstanceId]);
        }

        await CorpseCustody.AdvanceCorpseRevisionAsync(context, corpse, cancellationToken);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.AddMetadataAudit(
            "corpse_loot_all_committed",
            null,
            new
            {
                command.CorpseId,
                ItemCount = plan.Steps.Select(step => step.Item.ItemInstanceId).Distinct().Count(),
                StepCount = plan.Steps.Count
            });
    }

    private static async Task ExecuteStepAsync(
        ItemTransactionContext context,
        LootAllState state,
        LootAllCorpseItemsCommand command,
        LootAllStep step,
        CancellationToken cancellationToken)
    {
        var source = await context.State.LoadItemAsync(step.Item.ItemInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("A planned corpse item disappeared inside its transaction.");
        var definition = await state.LoadDefinitionAsync(source.DefinitionId, cancellationToken);
        switch (step.Kind)
        {
            case LootAllStepKind.EquipBag:
            case LootAllStepKind.Equip:
                {
                    var hasContents = await context.State.BagHasContentsAsync(source, cancellationToken);
                    if (hasContents)
                    {
                        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, source, cancellationToken);
                    }

                    var ownsSection = await CorpseBagCustody.OwnsSectionAsync(
                        context,
                        command.CorpseId,
                        source,
                        cancellationToken);
                    await context.Placement.EnsureEquipmentDestinationAsync(
                        command.CharacterId,
                        step.EquipmentSlotId,
                        definition,
                        source.Quantity,
                        hasContents,
                        cancellationToken);
                    await CorpseCustodyWrites.EquipCorpseItemAsync(
                        context,
                        source,
                        command.CharacterId,
                        step.EquipmentSlotId,
                        cancellationToken);
                    if (ownsSection)
                    {
                        await CorpseBagCustody.DetachSectionAsync(context, command.CorpseId, source, cancellationToken);
                    }

                    return;
                }

            case LootAllStepKind.TopUpEquipped:
                {
                    var target = await LoadTargetAsync(context, command, step, cancellationToken);
                    await context.Placement.EnsureEquippedStackQuantityAsync(
                        command.CharacterId,
                        step.EquipmentSlotId,
                        definition,
                        target.Quantity + step.Quantity,
                        cancellationToken);
                    await CorpseCustodyWrites.MergeCorpseQuantityAsync(
                        context,
                        source,
                        target,
                        step.Quantity,
                        !step.TakesRest,
                        cancellationToken);
                    return;
                }

            case LootAllStepKind.Merge:
                {
                    var target = await LoadTargetAsync(context, command, step, cancellationToken);
                    var destination = await context.State.LoadOwnedContainerAsync(
                        target.ContainerId!.Value,
                        command.CharacterId,
                        cancellationToken);
                    await context.Placement.SelectDestinationSlotAsync(
                        destination,
                        definition,
                        target.Quantity + step.Quantity,
                        target.ContainerSlotIndex,
                        false,
                        null,
                        false,
                        cancellationToken,
                        target.ItemInstanceId);
                    await CorpseCustodyWrites.MergeCorpseQuantityAsync(
                        context,
                        source,
                        target,
                        step.Quantity,
                        !step.TakesRest,
                        cancellationToken);
                    context.Journal.TouchContainer(destination.ContainerId);
                    return;
                }

            case LootAllStepKind.Place:
                {
                    var destination = await context.State.LoadOwnedContainerAsync(
                        step.Destination.ContainerId,
                        command.CharacterId,
                        cancellationToken);
                    CorpseItemTransfers.EnsureCarriedLootDestination(destination);
                    if (source.IsBag && await context.State.BagHasContentsAsync(source, cancellationToken))
                    {
                        ItemTransactionContext.Reject(
                            ItemTransactionErrorCodes.BagNotEmpty,
                            "A corpse Bag with contents cannot enter an ordinary slot.");
                    }

                    await context.Placement.SelectDestinationSlotAsync(
                        destination,
                        definition,
                        step.Quantity,
                        step.Destination.SlotIndex,
                        false,
                        source.IsBag ? source.ItemInstanceId : null,
                        false,
                        cancellationToken);
                    if (step.TakesRest)
                    {
                        var ownsSection = await CorpseBagCustody.OwnsSectionAsync(
                            context,
                            command.CorpseId,
                            source,
                            cancellationToken);
                        await CorpseCustodyWrites.MoveCorpseItemAsync(
                            context,
                            source,
                            destination,
                            step.Destination.SlotIndex,
                            cancellationToken);
                        if (ownsSection)
                        {
                            await CorpseBagCustody.DetachSectionAsync(context, command.CorpseId, source, cancellationToken);
                        }
                    }
                    else
                    {
                        await CorpseCustodyWrites.SplitCorpseQuantityAsync(
                            context,
                            source,
                            await context.State.LoadPoliciesAsync(source.ItemInstanceId, cancellationToken),
                            step.Quantity,
                            destination,
                            step.Destination.SlotIndex,
                            cancellationToken);
                    }

                    context.Journal.TouchContainer(destination.ContainerId);
                    return;
                }

            default:
                throw new InvalidOperationException($"Unknown Loot all step '{step.Kind}'.");
        }
    }

    private static async Task<LockedItem> LoadTargetAsync(
        ItemTransactionContext context,
        LootAllCorpseItemsCommand command,
        LootAllStep step,
        CancellationToken cancellationToken)
    {
        var target = await context.State.LoadItemAsync(step.TargetItemInstanceId, cancellationToken);
        if (target is null || target.OwningCharacterId != command.CharacterId)
        {
            throw new InvalidOperationException("A planned target stack is not the looter's inside its transaction.");
        }

        return target;
    }

    // Every corpse item in section and slot order, with the contents slots of every Bag.
    private static async Task<IReadOnlyList<LootAllCorpseEntry>> LoadCorpseItemsAsync(
        ItemTransactionContext context,
        LootAllState state,
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        var rows = (await context.Connection.QueryAsync<CorpseItemRow>(new CommandDefinition(
            """
            select
                section.section_kind as "SectionKind",
                section.container_id as "SectionContainerId",
                item.id as "ItemInstanceId",
                item.definition_id as "DefinitionId",
                item.quantity as "Quantity",
                bag.carry_capacity_bonus as "BagCarryCapacityBonus",
                bag_contents.id as "BagContentsContainerId"
            from corpse_sections section
            join item_instances item on item.container_id = section.container_id
            left join bag_definitions bag on bag.definition_id = item.definition_id
            left join item_containers bag_contents
              on bag_contents.bound_bag_item_instance_id = item.id
             and bag_contents.container_type = 'bag_contents'
            where section.corpse_id = @CorpseId
            order by
                case section.section_kind
                    when 'equipment' then 0
                    when 'general_inventory' then 1
                    else 2
                end,
                item.container_slot_index;
            """,
            new { CorpseId = corpseId },
            context.Transaction,
            cancellationToken: cancellationToken))).ToArray();

        var entries = new List<LootAllCorpseEntry>(rows.Length);
        foreach (var row in rows)
        {
            var definition = await state.LoadDefinitionAsync(row.DefinitionId, cancellationToken);
            var policies = await context.State.LoadPoliciesAsync(row.ItemInstanceId, cancellationToken);
            var capabilities = ItemPolicyRules.Evaluate(
                definition.RuntimeDefinition,
                ItemPolicyStates.ToPolicyStates(policies));
            LootAllBag? bag = null;
            if (row.BagCarryCapacityBonus is long bonus)
            {
                bag = new LootAllBag(
                    bonus,
                    row.BagContentsContainerId is Guid contentsId
                        ? await state.LoadSlotsAsync(contentsId, ItemContainerTypeIds.BagContents, cancellationToken)
                        : []);
            }

            entries.Add(new LootAllCorpseEntry(
                new LootAllCorpseItem(
                    row.ItemInstanceId,
                    row.DefinitionId,
                    row.Quantity,
                    row.SectionKind,
                    LootAllState.MergeKey(definition, policies),
                    capabilities.CanChangeOwningCharacter,
                    bag),
                row.SectionContainerId));
        }

        return entries;
    }

    private sealed record LootAllCorpseEntry(LootAllCorpseItem Item, Guid SectionContainerId);

    private sealed class CorpseItemRow
    {
        public string SectionKind { get; set; } = string.Empty;

        public Guid SectionContainerId { get; set; }

        public Guid ItemInstanceId { get; set; }

        public string DefinitionId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public long? BagCarryCapacityBonus { get; set; }

        public Guid? BagContentsContainerId { get; set; }
    }
}
