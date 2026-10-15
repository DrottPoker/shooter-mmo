using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Grants live Mob loot to the looting character under a derived item id.
internal sealed class MobLootGrantCommandHandler
    : IItemCommandHandler<GrantMobLootCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        CancellationToken cancellationToken)
    {
        if (context.Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || command.GrantId == Guid.Empty
            || command.GrantId != context.OperationId
            || command.SourceCorpseId == Guid.Empty
            || !MobLootRules.IsMobIdentifier(command.SourceActorDefinitionId)
            || command.CharacterId == Guid.Empty
            || command.ExpectedCharacterRevision < 0
            || command.DestinationContainerId == Guid.Empty
            || command.ExpectedDestinationContainerRevision < 0
            || command.DestinationSlotIndex < 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "The live Mob loot grant is incomplete or invalid.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(
                command.CharacterId,
                command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
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

        var slotIndex = await context.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var itemInstanceId = MobLootRules.CreateScopedGuid("mob-loot-item", command.GrantId);
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
        var afterState = await context.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "live_mob_loot_granted",
            itemInstanceId,
            null,
            afterState);
        context.AddMetadataAudit(
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
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(itemInstanceId);
    }
}
