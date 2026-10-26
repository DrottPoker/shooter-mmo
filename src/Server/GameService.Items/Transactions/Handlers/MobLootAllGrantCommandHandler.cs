using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Loot all for a live Mob corpse: grants every loot entry that fits, whole, under the item id
// derived from its grant id. The worker holds the corpse in memory, so nothing ever swaps and
// no stack is split; the grant ledger keeps an entry from being granted twice.
internal sealed class MobLootAllGrantCommandHandler : IItemCommandHandler<GrantMobLootAllCommand>
{
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
        foreach (var loot in command.Loot.Where(loot => !alreadyGranted.Contains(loot.GrantId)))
        {
            var definition = await state.LoadDefinitionAsync(loot.DefinitionId, cancellationToken);
            MobLootRules.ValidateMobLootDefinition(definition, loot.Quantity);
            var itemInstanceId = MobLootWrites.ItemInstanceId(loot.GrantId);
            grants.Add(itemInstanceId, loot);
            items.Add(new LootAllCorpseItem(
                itemInstanceId,
                loot.DefinitionId,
                loot.Quantity,
                CorpseSectionKindIds.GeneralInventory,
                LootAllState.MergeKey(definition, []),
                true));
        }

        var plan = CorpseLootAllPlanner.Plan(new LootAllRequest(
            state.FindDefinition,
            state.MeetsLevelRequirement,
            character.CarriedWeight,
            character.CarryCapacity,
            looter.Equipment,
            looter.PermanentInventory,
            looter.BagContents,
            items,
            true));
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
            await ExecuteStepAsync(context, state, command, grant, step, cancellationToken);
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

    private static async Task ExecuteStepAsync(
        ItemTransactionContext context,
        LootAllState state,
        GrantMobLootAllCommand command,
        MobLootGrantSource grant,
        LootAllStep step,
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
                    await MobLootWrites.CreateInContainerAsync(
                        context,
                        grant,
                        definition,
                        step.Quantity,
                        destination.ContainerId,
                        slotIndex,
                        cancellationToken);
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
