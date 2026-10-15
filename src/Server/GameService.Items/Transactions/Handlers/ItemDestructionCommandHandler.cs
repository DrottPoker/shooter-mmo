using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Player destruction of an owned item.
internal sealed class ItemDestructionCommandHandler
    : IItemCommandHandler<DestroyItemCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        DestroyItemCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemDestroyForbidden,
                "An item destruction reason is required.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        if (string.Equals(item.SourceContainerType, "recovery_storage", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                "Recovery Storage items cannot use player destruction.");
        }

        var definition = await context.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var policies = await context.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        var capabilities = ItemPolicyRules.Evaluate(
            definition.RuntimeDefinition,
            ItemPolicyStates.ToPolicyStates(policies));
        if (string.Equals(
                definition.RuntimeDefinition.Category,
                ItemCategoryIds.QuestItem,
                StringComparison.Ordinal)
            && capabilities.DeathDisposition == ItemDeathDisposition.ProtectedRecovery)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "A protected quest item cannot use direct player destruction.");
        }

        if (!capabilities.CanPlayerDestroy)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemDestroyForbidden,
                "The item definition does not permit player destruction.");
        }

        if (await context.BagHasContentsAsync(item, cancellationToken))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagNotEmpty,
                "A non-empty Bag cannot be destroyed.");
        }

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
                @Reason);

            delete from item_instances
            where id = @ItemInstanceId;
            """,
            new
            {
                item.ItemInstanceId,
                item.DefinitionId,
                context.OperationId,
                item.Quantity,
                Reason = command.Reason.Trim()
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        context.AddItemAudit(
            "item_destroyed",
            item.ItemInstanceId,
            beforeState,
            null);
        context.TouchContainer(item.ContainerId);
        context.TouchCharacter(command.CharacterId);
    }
}
