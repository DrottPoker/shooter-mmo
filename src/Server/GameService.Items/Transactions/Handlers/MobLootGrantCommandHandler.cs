using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Grants live Mob loot to the looting character under a derived item id, into a container slot
// or straight into an empty equipment slot.
internal sealed class MobLootGrantCommandHandler
    : IItemCommandHandler<GrantMobLootCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        CancellationToken cancellationToken)
    {
        var toEquipment = command.EquipmentSlotId is not null;
        if (context.Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || command.GrantId == Guid.Empty
            || command.GrantId != context.OperationId
            || command.SourceCorpseId == Guid.Empty
            || !Identifiers.IsContentIdentifier(command.SourceActorDefinitionId)
            || command.CharacterId == Guid.Empty
            || command.ExpectedCharacterRevision < 0
            || (toEquipment
                ? string.IsNullOrWhiteSpace(command.EquipmentSlotId)
                    || !MobLootRules.NamesEquipmentDestinationOnly(
                        command.DestinationContainerId,
                        command.ExpectedDestinationContainerRevision,
                        command.DestinationSlotIndex)
                : command.DestinationContainerId == Guid.Empty
                    || command.ExpectedDestinationContainerRevision < 0
                    || command.DestinationSlotIndex < 0))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "The live Mob loot grant is incomplete or invalid.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(
                command.CharacterId,
                command.ExpectedCharacterRevision)],
            cancellationToken);
        if (toEquipment)
        {
            await GrantIntoEquipmentAsync(context, command, cancellationToken);
            return;
        }

        await context.Locks.LockMutationScopeAsync(
            [],
            [command.DestinationContainerId],
            cancellationToken);
        var definition = await context.State.LoadDefinitionAsync(
            command.DefinitionId,
            cancellationToken);
        MobLootRules.ValidateMobLootDefinition(definition, command.Quantity);
        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Mob loot destination container revision is stale.");
        }

        var slotIndex = await context.Placement.SelectDestinationSlotAsync(
            destination,
            definition,
            command.Quantity,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var itemInstanceId = ScopedGuid.Create("mob-loot-item", command.GrantId);
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
        await JournalGrantAsync(context, command, itemInstanceId, cancellationToken);
        context.Journal.TouchContainer(destination.ContainerId);
    }

    // Live Mob loot never swaps or merges, because the worker holds the corpse in memory and
    // cannot take an item back, so the equipment slot must be empty.
    private static async Task GrantIntoEquipmentAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        CancellationToken cancellationToken)
    {
        var definition = await context.State.LoadDefinitionAsync(
            command.DefinitionId,
            cancellationToken);
        MobLootRules.ValidateMobLootDefinition(definition, command.Quantity);
        var equipmentSlotId = command.EquipmentSlotId!;
        await context.Placement.EnsureEquipmentDestinationAsync(
            command.CharacterId,
            equipmentSlotId,
            definition,
            command.Quantity,
            false,
            cancellationToken);
        var itemInstanceId = ScopedGuid.Create("mob-loot-item", command.GrantId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @Quantity,
                @CharacterId,
                @EquipmentSlotId);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = definition.RuntimeDefinition.Id,
                command.Quantity,
                command.CharacterId,
                EquipmentSlotId = equipmentSlotId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalGrantAsync(context, command, itemInstanceId, cancellationToken);
    }

    private static async Task JournalGrantAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        Guid itemInstanceId,
        CancellationToken cancellationToken)
    {
        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "live_mob_loot_granted",
            itemInstanceId,
            null,
            afterState);
        context.Journal.AddMetadataAudit(
            "live_mob_loot_grant_committed",
            null,
            new
            {
                command.GrantId,
                command.SourceCorpseId,
                command.SourceActorDefinitionId,
                command.CharacterId,
                ItemInstanceId = itemInstanceId
            });
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(itemInstanceId);
    }
}
