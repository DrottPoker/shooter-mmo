using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Applies protection and insurance policies and removes insurance. NPC insurance
// charges character currency through the Characters wallet in the same transaction.
internal sealed class ItemPolicyCommandHandler(ICharacterWallet characterWallet)
    : IItemCommandHandler<ApplyItemPolicyCommand>,
    IItemCommandHandler<RemoveInsurancePolicyCommand>
{
    private readonly ICharacterWallet characterWallet = characterWallet;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ApplyItemPolicyCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.PolicyKind)
            || string.IsNullOrWhiteSpace(command.SourceKind)
            || string.IsNullOrWhiteSpace(command.SourceId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "Policy kind and source lineage are required.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        var definition = await context.State.LoadDefinitionAsync(item.DefinitionId, cancellationToken);
        var policyKind = command.PolicyKind.Trim();
        if (string.Equals(policyKind, ItemPolicyIds.Insured, StringComparison.Ordinal))
        {
            if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker)
            {
                context.Authority.EnsureInsuranceNpcAccess();
                if (command.Price <= 0
                    || !string.Equals(
                        command.SourceKind.Trim(),
                        ItemPolicySourceKinds.InsuranceService,
                        StringComparison.Ordinal))
                {
                    ItemTransactionContext.Reject(
                        ItemTransactionErrorCodes.ItemPolicyRestricted,
                        "NPC insurance requires a positive server-owned price and insurance lineage.");
                }
            }
            else
            {
                context.Authority.EnsureSystemAuthority();
                if (command.Price != 0)
                {
                    ItemTransactionContext.Reject(
                        ItemTransactionErrorCodes.ItemPolicyRestricted,
                        "System insurance fixtures cannot charge character currency.");
                }
            }

            if (!ItemPolicyRules.CanApplyInsurance(definition.RuntimeDefinition))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemPolicyRestricted,
                    "Insurance requires a non-stackable equipment-compatible item definition.");
            }
        }
        else if (!string.Equals(
                     policyKind,
                     ItemPolicyIds.ProtectedOnDeath,
                     StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "The requested item policy kind is unsupported.");
        }
        else
        {
            context.Authority.EnsureSystemAuthority();
            if (command.Price != 0)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.ItemPolicyRestricted,
                    "Protected item grants cannot charge insurance currency.");
            }
        }

        var policies = await context.State.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        if (policies.Any(policy =>
                string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)
                && string.Equals(policy.PolicyKind, policyKind, StringComparison.Ordinal)))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyRestricted,
                "The item already has an active policy of the requested kind.");
        }

        if (command.Price > 0)
        {
            var debit = await characterWallet.DebitAsync(
                context.Scope,
                command.CharacterId,
                command.Price,
                cancellationToken);
            if (!debit.Succeeded)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.CurrencyInsufficient,
                    "The character does not have enough currency for this insurance policy.");
            }

            context.Journal.AddMetadataAudit(
                "item_insurance_currency_charged",
                new { command.CharacterId, Currency = debit.Before },
                new { command.CharacterId, Currency = debit.After, command.Price });
        }

        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
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

            update item_instances
            set revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new
            {
                PolicyId = Guid.NewGuid(),
                item.ItemInstanceId,
                PolicyKind = policyKind,
                SourceKind = command.SourceKind.Trim(),
                SourceId = command.SourceId.Trim()
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit("item_policy_applied", item.ItemInstanceId, beforeState, afterState);
        context.Journal.TouchContainer(item.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(item.ItemInstanceId);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        RemoveInsurancePolicyCommand command,
        CancellationToken cancellationToken)
    {
        if (context.Actor.Authority == ItemTransactionAuthority.SimulationWorker)
        {
            context.Authority.EnsureInsuranceNpcAccess();
        }
        else
        {
            context.Authority.EnsureSystemAuthority();
        }
        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.ItemInstanceId],
            [],
            cancellationToken);
        var item = await context.State.LoadOwnedItemAsync(
            command.ItemInstanceId,
            command.CharacterId,
            command.ExpectedItemRevision,
            cancellationToken);
        var policies = await context.State.LoadPoliciesAsync(item.ItemInstanceId, cancellationToken);
        var insurance = policies.SingleOrDefault(policy =>
            string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal)
            && string.Equals(policy.PolicyKind, ItemPolicyIds.Insured, StringComparison.Ordinal));
        if (insurance is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemPolicyNotFound,
                "The item does not have active insurance.");
        }

        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instance_policies
            set status = 'removed',
                revision = revision + 1,
                removed_at = now()
            where id = @PolicyId;

            update item_instances
            set revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new
            {
                insurance.PolicyId,
                item.ItemInstanceId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            item.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit("item_insurance_removed", item.ItemInstanceId, beforeState, afterState);
        context.Journal.TouchContainer(item.ContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.IncludeResultItem(item.ItemInstanceId);
    }
}
