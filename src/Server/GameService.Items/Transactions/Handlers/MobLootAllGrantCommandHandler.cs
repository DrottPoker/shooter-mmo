using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Loot all for a live Mob corpse: grants every loot entry that fits, whole, under the item id
// derived from its grant id. The worker holds the corpse in memory, so nothing ever swaps and
// no stack is split; the grant ledger keeps an entry from being granted twice. A quest drop
// takes its quest's lineage and only the quantity that the quest still needs, and one that the
// quest no longer needs stays in the corpse.
internal sealed class MobLootAllGrantCommandHandler(QuestDrops questDrops) : IItemCommandHandler<GrantMobLootAllCommand>
{
    private readonly QuestDrops questDrops = questDrops;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        GrantMobLootAllCommand command,
        CancellationToken cancellationToken)
    {
        Validate(context, command);
        var characters = await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var character = characters[command.CharacterId];
        var state = new LootAllState(context);
        var looter = await state.LoadLooterAsync(character, cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            looter.ItemInstanceIds,
            looter.BagContentsContainerId is Guid bagId
                ? [character.PermanentInventoryContainerId, bagId]
                : [character.PermanentInventoryContainerId],
            cancellationToken);

        var alreadyGranted = (await context.Connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select grant_id
                from live_mob_loot_grants
                where grant_id = any(@GrantIds);
                """,
                new { GrantIds = command.Loot.Select(loot => loot.GrantId).ToArray() },
                context.Transaction,
                cancellationToken: cancellationToken)))
            .ToHashSet();
        var grants = new Dictionary<Guid, MobLootGrantEntry>();
        var items = new List<LootAllCorpseItem>();
        var unneededQuestDrops = 0;
        var questDropItems = new Dictionary<Guid, PlannedQuestDrop>();
        foreach (var loot in command.Loot.Where(loot => !alreadyGranted.Contains(loot.GrantId)))
        {
            var definition = await state.LoadDefinitionAsync(loot.DefinitionId, cancellationToken);
            var quantity = loot.Quantity;
            IReadOnlyList<LockedItemPolicy> policies = [];
            if (loot.QuestId is null)
            {
                MobLootRules.ValidateMobLootDefinition(definition, loot.Quantity);
            }
            else
            {
                quantity = await NeededQuestDropAsync(context, command, loot, definition, cancellationToken);
                if (quantity == 0)
                {
                    unneededQuestDrops++;
                    continue;
                }

                policies = QuestDrops.LineagePolicies(definition, loot.QuestId);
                questDropItems.Add(
                    MobLootWrites.ItemInstanceId(loot.GrantId),
                    new PlannedQuestDrop(loot.QuestId, command.SourceActorDefinitionId, definition));
            }

            var itemInstanceId = MobLootWrites.ItemInstanceId(loot.GrantId);
            grants.Add(itemInstanceId, loot);
            items.Add(new LootAllCorpseItem(
                itemInstanceId,
                loot.DefinitionId,
                quantity,
                CorpseSectionKindIds.GeneralInventory,
                LootAllState.MergeKey(definition, policies),
                true));
        }

        if (items.Count == 0 && alreadyGranted.Count == 0 && unneededQuestDrops > 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestItemNotNeeded,
                "Your quests do not need what is left in the corpse.");
        }

        var plan = await questDrops.PlanLootAllAsync(
            context,
            command.CharacterId,
            items,
            questDropItems,
            planned => CorpseLootAllPlanner.Plan(new LootAllRequest(
                state.FindDefinition,
                state.MeetsLevelRequirement,
                character.CarriedWeight,
                character.CarryCapacity,
                looter.Equipment,
                looter.PermanentInventory,
                looter.BagContents,
                planned,
                true)),
            cancellationToken);
        if (plan.Steps.Count == 0 && alreadyGranted.Count == 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.InventoryFull,
                "Nothing in the corpse fits in your equipment or inventory.");
        }

        foreach (var step in plan.Steps)
        {
            var loot = grants[step.Item.ItemInstanceId];
            var grant = new MobLootGrantSource(
                loot.GrantId,
                command.SourceCorpseId,
                command.SourceActorDefinitionId,
                command.CharacterId);
            await MobLootRules.RecordGrantAsync(
                context,
                loot.GrantId,
                command.CharacterId,
                command.SourceCorpseId,
                cancellationToken);
            await ExecuteStepAsync(context, state, command, grant, step, loot.QuestId, cancellationToken);
        }

        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.AddMetadataAudit(
            "live_mob_loot_all_committed",
            null,
            new
            {
                command.SourceCorpseId,
                GrantedCount = plan.Steps.Count,
                AlreadyGrantedCount = alreadyGranted.Count
            });
    }

    // A quest drop is a protected quest item within its stack limit, granted up to the quantity
    // that its quest still needs; drops of one item share that in the plan.
    private async Task<int> NeededQuestDropAsync(
        ItemTransactionContext context,
        GrantMobLootAllCommand command,
        MobLootGrantEntry loot,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!QuestDrops.IsQuestDropItem(definition)
            || loot.Quantity > definition.RuntimeDefinition.MaximumStackSize)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "A quest drop must be a protected quest item within its stack limit.");
        }

        return await questDrops.NeededAsync(
            context,
            command.CharacterId,
            loot.QuestId!,
            command.SourceActorDefinitionId,
            definition,
            loot.Quantity,
            cancellationToken);
    }

    private static async Task ExecuteStepAsync(
        ItemTransactionContext context,
        LootAllState state,
        GrantMobLootAllCommand command,
        MobLootGrantSource grant,
        LootAllStep step,
        string? questId,
        CancellationToken cancellationToken)
    {
        var definition = await state.LoadDefinitionAsync(step.Item.DefinitionId, cancellationToken);
        switch (step.Kind)
        {
            case LootAllStepKind.Equip:
                await context.Placement.EnsureEquipmentDestinationAsync(
                    command.CharacterId,
                    step.EquipmentSlotId,
                    definition,
                    step.Quantity,
                    false,
                    cancellationToken);
                await MobLootWrites.CreateEquippedAsync(
                    context,
                    grant,
                    definition,
                    step.Quantity,
                    step.EquipmentSlotId,
                    cancellationToken);
                return;
            case LootAllStepKind.TopUpEquipped:
                {
                    var target = await LoadTargetAsync(context, command, step, cancellationToken);
                    await context.Placement.EnsureEquippedStackQuantityAsync(
                        command.CharacterId,
                        step.EquipmentSlotId,
                        definition,
                        target.Quantity + step.Quantity,
                        cancellationToken);
                    await MobLootWrites.TopUpAsync(context, grant, target.ItemInstanceId, step.Quantity, cancellationToken);
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
                    await MobLootWrites.TopUpAsync(context, grant, target.ItemInstanceId, step.Quantity, cancellationToken);
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
                    var slotIndex = await context.Placement.SelectDestinationSlotAsync(
                        destination,
                        definition,
                        step.Quantity,
                        step.Destination.SlotIndex,
                        false,
                        null,
                        false,
                        cancellationToken);
                    if (questId is null)
                    {
                        await MobLootWrites.CreateInContainerAsync(
                            context,
                            grant,
                            definition,
                            step.Quantity,
                            destination.ContainerId,
                            slotIndex,
                            cancellationToken);
                    }
                    else
                    {
                        await MobLootWrites.CreateQuestDropInContainerAsync(
                            context,
                            grant,
                            definition,
                            step.Quantity,
                            destination.ContainerId,
                            slotIndex,
                            questId,
                            cancellationToken);
                    }

                    return;
                }

            default:
                throw new InvalidOperationException($"Live Mob loot cannot take the Loot all step '{step.Kind}'.");
        }
    }

    private static async Task<LockedItem> LoadTargetAsync(
        ItemTransactionContext context,
        GrantMobLootAllCommand command,
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

    private static void Validate(ItemTransactionContext context, GrantMobLootAllCommand command)
    {
        if (context.Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || command.CharacterId == Guid.Empty
            || command.ExpectedCharacterRevision < 0
            || command.SourceCorpseId == Guid.Empty
            || !Identifiers.IsContentIdentifier(command.SourceActorDefinitionId)
            || command.Loot is null
            || command.Loot.Count is < 1 or > WorldActorCorpseRules.MaximumLootEntries
            || command.Loot.Any(loot =>
                loot.GrantId == Guid.Empty
                || !Identifiers.IsContentIdentifier(loot.DefinitionId)
                || loot.Quantity <= 0)
            || command.Loot.Select(loot => loot.GrantId).Distinct().Count() != command.Loot.Count)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "The live Mob Loot all grant is incomplete or invalid.");
        }
    }
}
