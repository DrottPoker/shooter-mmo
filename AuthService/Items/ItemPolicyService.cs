using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public sealed class ItemPolicyService(ItemTransactionService transactionService)
{
    public Task<ItemTransactionResult> ApplyProtectedOnDeathAsync(
        Guid operationId,
        Guid characterId,
        long expectedCharacterRevision,
        Guid itemInstanceId,
        long expectedItemRevision,
        string sourceKind,
        string sourceId,
        CancellationToken cancellationToken)
    {
        return ApplyAsync(
            operationId,
            characterId,
            expectedCharacterRevision,
            itemInstanceId,
            expectedItemRevision,
            ItemPolicyIds.ProtectedOnDeath,
            sourceKind,
            sourceId,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ApplyInsuranceAsync(
        Guid operationId,
        Guid characterId,
        long expectedCharacterRevision,
        Guid itemInstanceId,
        long expectedItemRevision,
        string insuranceGrantId,
        CancellationToken cancellationToken)
    {
        return ApplyAsync(
            operationId,
            characterId,
            expectedCharacterRevision,
            itemInstanceId,
            expectedItemRevision,
            ItemPolicyIds.Insured,
            ItemPolicySourceKinds.InsuranceService,
            insuranceGrantId,
            cancellationToken);
    }

    public Task<ItemTransactionResult> RemoveInsuranceAsync(
        Guid operationId,
        Guid characterId,
        long expectedCharacterRevision,
        Guid itemInstanceId,
        long expectedItemRevision,
        CancellationToken cancellationToken)
    {
        return transactionService.ExecuteAsync(
            new ItemTransactionRequest<RemoveInsurancePolicyCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new RemoveInsurancePolicyCommand(
                    characterId,
                    expectedCharacterRevision,
                    itemInstanceId,
                    expectedItemRevision)),
            cancellationToken);
    }

    private Task<ItemTransactionResult> ApplyAsync(
        Guid operationId,
        Guid characterId,
        long expectedCharacterRevision,
        Guid itemInstanceId,
        long expectedItemRevision,
        string policyKind,
        string sourceKind,
        string sourceId,
        CancellationToken cancellationToken)
    {
        return transactionService.ExecuteAsync(
            new ItemTransactionRequest<ApplyItemPolicyCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new ApplyItemPolicyCommand(
                    characterId,
                    expectedCharacterRevision,
                    itemInstanceId,
                    expectedItemRevision,
                    policyKind,
                    sourceKind,
                    sourceId)),
            cancellationToken);
    }
}
