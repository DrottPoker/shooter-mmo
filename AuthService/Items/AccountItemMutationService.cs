using AuthService.Http;

namespace AuthService.Items;

public sealed class AccountItemMutationService(ItemTransactionService transactionService)
{
    public Task<ServiceResult<ItemTransactionResult>> RelocateAsync(
        Guid accountId,
        Guid characterId,
        RelocateAccountItemRequest request,
        CancellationToken cancellationToken)
    {
        var invalidOperation = ValidateOperationId(request.OperationId);
        if (invalidOperation is not null)
        {
            return Task.FromResult(invalidOperation);
        }

        return MapResultAsync(transactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                request.OperationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new RelocateItemCommand(
                    characterId,
                    request.ExpectedCharacterRevision,
                    request.ItemInstanceId,
                    request.ExpectedItemRevision,
                    request.DestinationContainerId,
                    request.DestinationSlotIndex)),
            cancellationToken));
    }

    public Task<ServiceResult<ItemTransactionResult>> SplitAsync(
        Guid accountId,
        Guid characterId,
        SplitAccountItemStackRequest request,
        CancellationToken cancellationToken)
    {
        var invalidOperation = ValidateOperationId(request.OperationId);
        if (invalidOperation is not null)
        {
            return Task.FromResult(invalidOperation);
        }

        return MapResultAsync(transactionService.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                request.OperationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new SplitItemStackCommand(
                    characterId,
                    request.ExpectedCharacterRevision,
                    request.ItemInstanceId,
                    request.ExpectedItemRevision,
                    request.Quantity,
                    request.DestinationContainerId,
                    request.DestinationSlotIndex)),
            cancellationToken));
    }

    public Task<ServiceResult<ItemTransactionResult>> MergeAsync(
        Guid accountId,
        Guid characterId,
        MergeAccountItemStacksRequest request,
        CancellationToken cancellationToken)
    {
        var invalidOperation = ValidateOperationId(request.OperationId);
        if (invalidOperation is not null)
        {
            return Task.FromResult(invalidOperation);
        }

        return MapResultAsync(transactionService.ExecuteAsync(
            new ItemTransactionRequest<MergeItemStacksCommand>(
                request.OperationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new MergeItemStacksCommand(
                    characterId,
                    request.ExpectedCharacterRevision,
                    request.SourceItemInstanceId,
                    request.ExpectedSourceItemRevision,
                    request.TargetItemInstanceId,
                    request.ExpectedTargetItemRevision)),
            cancellationToken));
    }

    public Task<ServiceResult<ItemTransactionResult>> DestroyAsync(
        Guid accountId,
        Guid characterId,
        DestroyAccountItemRequest request,
        CancellationToken cancellationToken)
    {
        var invalidOperation = ValidateOperationId(request.OperationId);
        if (invalidOperation is not null)
        {
            return Task.FromResult(invalidOperation);
        }

        return MapResultAsync(transactionService.ExecuteAsync(
            new ItemTransactionRequest<DestroyItemCommand>(
                request.OperationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new DestroyItemCommand(
                    characterId,
                    request.ExpectedCharacterRevision,
                    request.ItemInstanceId,
                    request.ExpectedItemRevision,
                    "player_destroyed")),
            cancellationToken));
    }

    public Task<ServiceResult<ItemTransactionResult>> ClaimRecoveryAsync(
        Guid accountId,
        Guid characterId,
        Guid deliveryId,
        ClaimAccountRecoveryDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var invalidOperation = ValidateOperationId(request.OperationId);
        if (invalidOperation is not null)
        {
            return Task.FromResult(invalidOperation);
        }

        if (request.Items is null)
        {
            return Task.FromResult(ServiceResult<ItemTransactionResult>.BadRequest(
                ItemApiErrorCodes.RecoveryClaimItemsRequired,
                "Recovery claim items are required."));
        }

        return MapResultAsync(transactionService.ExecuteAsync(
            new ItemTransactionRequest<ClaimRecoveryDeliveryCommand>(
                request.OperationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new ClaimRecoveryDeliveryCommand(
                    characterId,
                    request.ExpectedCharacterRevision,
                    deliveryId,
                    request.ExpectedRecoveryDeliveryRevision,
                    request.DestinationContainerId,
                    request.Items)),
            cancellationToken));
    }

    public Task<ServiceResult<ItemTransactionResult>> ChangeSecureContainerTierAsync(
        Guid accountId,
        ChangeAccountSecureContainerTierRequest request,
        CancellationToken cancellationToken)
    {
        var invalidOperation = ValidateOperationId(request.OperationId);
        if (invalidOperation is not null)
        {
            return Task.FromResult(invalidOperation);
        }

        if (string.IsNullOrWhiteSpace(request.TierId)
            || request.CharacterRevisions is null)
        {
            return Task.FromResult(ServiceResult<ItemTransactionResult>.BadRequest(
                ItemApiErrorCodes.SecureTierRequestInvalid,
                "A Secure Container tier and all character revisions are required."));
        }

        return MapResultAsync(transactionService.ExecuteAsync(
            new ItemTransactionRequest<ChangeSecureContainerTierCommand>(
                request.OperationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new ChangeSecureContainerTierCommand(
                    accountId,
                    request.ExpectedEntitlementRevision,
                    request.TierId.Trim(),
                    request.CharacterRevisions)),
            cancellationToken));
    }

    private static ServiceResult<ItemTransactionResult>? ValidateOperationId(Guid operationId)
    {
        return operationId == Guid.Empty
            ? ServiceResult<ItemTransactionResult>.BadRequest(
                ItemApiErrorCodes.OperationIdRequired,
                "An item operation id is required.")
            : null;
    }

    private static async Task<ServiceResult<ItemTransactionResult>> MapResultAsync(
        Task<ItemTransactionResult> operation)
    {
        return MapResult(await operation);
    }

    private static ServiceResult<ItemTransactionResult> MapResult(ItemTransactionResult result)
    {
        if (result.Succeeded)
        {
            return ServiceResult<ItemTransactionResult>.Ok(result);
        }

        var error = result.Error
            ?? throw new InvalidOperationException("A rejected item operation has no error.");
        return error.Code switch
        {
            ItemTransactionErrorCodes.AuthorityRequired =>
                ServiceResult<ItemTransactionResult>.Forbidden(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemNotFound or
            ItemTransactionErrorCodes.ItemNotOwned or
            ItemTransactionErrorCodes.RecoveryDeliveryNotFound or
            ItemTransactionErrorCodes.ItemPolicyNotFound =>
                ServiceResult<ItemTransactionResult>.NotFound(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemStateConflict or
            ItemTransactionErrorCodes.ItemOperationConflict or
            ItemTransactionErrorCodes.ItemTransactionTimeout or
            ItemTransactionErrorCodes.ItemSlotOccupied or
            ItemTransactionErrorCodes.EquipmentSlotOccupied or
            ItemTransactionErrorCodes.ItemQuantityChanged or
            ItemTransactionErrorCodes.BagStateChanged or
            ItemTransactionErrorCodes.OfflineAccessRequired =>
                ServiceResult<ItemTransactionResult>.Conflict(error.Code, error.Message),
            _ => ServiceResult<ItemTransactionResult>.UnprocessableEntity(
                error.Code,
                error.Message)
        };
    }
}
