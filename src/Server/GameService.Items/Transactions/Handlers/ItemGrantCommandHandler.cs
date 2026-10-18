using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
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
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [],
            [command.DestinationContainerId],
            cancellationToken);

        var definition = await context.State.LoadDefinitionAsync(command.DefinitionId, cancellationToken);
        if (command.Quantity <= 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemQuantityChanged,
                "A grant quantity must be positive.");
        }

        if (command.Quantity > definition.RuntimeDefinition.MaximumStackSize)
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
                context.Authority.EnsureQuestNpcAccess();
            }
            else
            {
                context.Authority.EnsureSystemAuthority();
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
                    $"""
                    select item.id
                    from item_instances item
                    {ItemOwnershipSql.Joins}
                    join item_instance_policies policy
                      on policy.item_instance_id = item.id
                     and policy.policy_kind = @PolicyKind
                     and policy.status = @ActiveStatus
                    where {ItemOwnershipSql.OwningCharacterId} = @CharacterId
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
                        SourceId = policySourceId,
                        PolicyKind = ItemPolicyIds.ProtectedOnDeath,
                        ActiveStatus = ItemPolicyRules.ActiveStatus
                    },
                    context.Transaction,
                    cancellationToken: cancellationToken));
            if (existingQuestItemId is not null)
            {
                context.Journal.AddMetadataAudit(
                    "quest_grant_already_satisfied",
                    new { QuestGrantId = policySourceId, ItemInstanceId = existingQuestItemId },
                    new { QuestGrantId = policySourceId, ItemInstanceId = existingQuestItemId });
                context.Journal.IncludeResultItem(existingQuestItemId.Value);
                return;
            }
        }

        var destination = await context.State.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        var slotIndex = await context.Placement.SelectDestinationSlotAsync(
            destination,
            definition,
            command.Quantity,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var itemInstanceId = Guid.NewGuid();
        await NewItemInstances.CreateAsync(
            context,
            itemInstanceId,
            definition,
            command.Quantity,
            destination.ContainerId,
            slotIndex,
            policySourceKind,
            policySourceId,
            cancellationToken);

        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit("item_granted", itemInstanceId, null, afterState);
        context.Journal.TouchContainer(destination.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(itemInstanceId);
    }
}
