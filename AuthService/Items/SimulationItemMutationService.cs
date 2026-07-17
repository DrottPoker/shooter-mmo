using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Items;

public sealed class SimulationItemMutationService(ItemTransactionService transactionService)
{
    public const int MaximumRecoveryClaimItems = 24;

    public async Task<ServiceResult<ItemTransactionResult>> ExecuteAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                authenticatedWorkerId,
                request.WorkerId,
                StringComparison.Ordinal))
        {
            return ServiceResult<ItemTransactionResult>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot submit another worker's item operation.");
        }

        var validationError = ValidateCommon(simulationSessionId, request);
        if (validationError is not null)
        {
            return validationError;
        }

        var actor = ItemTransactionActor.ForSimulationWorker(
            request.AccountId,
            request.CharacterId,
            simulationSessionId,
            request.WorkerId!.Trim(),
            request.WorkerRuntimeId!.Trim(),
            request.ShardId!.Trim(),
            TokenGenerator.HashToken(request.SessionToken!.Trim()),
            new ItemTransactionLiveAccess(
                request.Access!.Bank,
                request.Access.RecoveryStorage,
                request.Access.InsuranceNpc));

        var operationKind = request.OperationKind!.Trim();
        var operation = operationKind switch
        {
            ItemOperationKinds.Relocate => ExecuteRelocateAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.Equip => ExecuteEquipAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.Unequip => ExecuteUnequipAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.SplitStack => ExecuteSplitAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.MergeStacks => ExecuteMergeAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.SwapContainerItems => ExecuteSwapAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.Destroy => ExecuteDestroyAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.ClaimRecoveryDelivery => ExecuteRecoveryClaimAsync(
                actor,
                request,
                cancellationToken),
            _ => null
        };

        return operation is null
            ? Invalid("The requested in-world item operation is not supported.")
            : MapResult(await operation);
    }

    private Task<ItemTransactionResult>? ExecuteRelocateAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItemAndDestination(request)
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<RelocateItemCommand>(
                    request.OperationId,
                    actor,
                    new RelocateItemCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.DestinationContainerId!.Value,
                        request.DestinationSlotIndex)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteEquipAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request) && !string.IsNullOrWhiteSpace(request.EquipmentSlotId)
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<EquipItemCommand>(
                    request.OperationId,
                    actor,
                    new EquipItemCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.EquipmentSlotId.Trim())),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteUnequipAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItemAndDestination(request)
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<UnequipItemCommand>(
                    request.OperationId,
                    actor,
                    new UnequipItemCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.DestinationContainerId!.Value,
                        request.DestinationSlotIndex)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteSplitAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItemAndDestination(request) && request.Quantity > 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<SplitItemStackCommand>(
                    request.OperationId,
                    actor,
                    new SplitItemStackCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.Quantity.Value,
                        request.DestinationContainerId!.Value,
                        request.DestinationSlotIndex)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteMergeAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request)
            && request.TargetItemInstanceId is not null
            && request.TargetItemInstanceId != Guid.Empty
            && request.ExpectedTargetItemRevision >= 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<MergeItemStacksCommand>(
                    request.OperationId,
                    actor,
                    new MergeItemStacksCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.TargetItemInstanceId.Value,
                        request.ExpectedTargetItemRevision!.Value)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteSwapAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request)
            && request.TargetItemInstanceId is not null
            && request.TargetItemInstanceId != Guid.Empty
            && request.ExpectedTargetItemRevision >= 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<SwapContainerItemsCommand>(
                    request.OperationId,
                    actor,
                    new SwapContainerItemsCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.TargetItemInstanceId.Value,
                        request.ExpectedTargetItemRevision!.Value)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteDestroyAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request)
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<DestroyItemCommand>(
                    request.OperationId,
                    actor,
                    new DestroyItemCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        "player_destroyed")),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteRecoveryClaimAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return request.RecoveryDeliveryId is not null
            && request.RecoveryDeliveryId != Guid.Empty
            && request.ExpectedRecoveryDeliveryRevision >= 0
            && request.DestinationContainerId is not null
            && request.DestinationContainerId != Guid.Empty
            && request.Items is { Count: > 0 and <= MaximumRecoveryClaimItems }
            && request.Items.All(item => item.ItemInstanceId != Guid.Empty && item.Revision >= 0)
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<ClaimRecoveryDeliveryCommand>(
                    request.OperationId,
                    actor,
                    new ClaimRecoveryDeliveryCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.RecoveryDeliveryId.Value,
                        request.ExpectedRecoveryDeliveryRevision!.Value,
                        request.DestinationContainerId.Value,
                        request.Items)),
                cancellationToken)
            : null;
    }

    private static ServiceResult<ItemTransactionResult>? ValidateCommon(
        Guid simulationSessionId,
        SimulationItemOperationRequest request)
    {
        if (simulationSessionId == Guid.Empty
            || request.OperationId == Guid.Empty
            || request.AccountId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || request.ExpectedCharacterRevision < 0
            || !IsValidIdentifier(request.WorkerId)
            || !IsValidIdentifier(request.WorkerRuntimeId)
            || !IsValidIdentifier(request.ShardId)
            || string.IsNullOrWhiteSpace(request.SessionToken)
            || request.SessionToken.Length > 1024
            || request.Access is null
            || string.IsNullOrWhiteSpace(request.OperationKind)
            || request.OperationKind.Length > 64)
        {
            return Invalid("The simulation item operation is incomplete or invalid.");
        }

        return null;
    }

    private static bool HasItem(SimulationItemOperationRequest request)
    {
        return request.ItemInstanceId is not null
            && request.ItemInstanceId != Guid.Empty
            && request.ExpectedItemRevision >= 0;
    }

    private static bool HasItemAndDestination(SimulationItemOperationRequest request)
    {
        return HasItem(request)
            && request.DestinationContainerId is not null
            && request.DestinationContainerId != Guid.Empty
            && request.DestinationSlotIndex is null or >= 0;
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static ServiceResult<ItemTransactionResult> Invalid(string message)
    {
        return ServiceResult<ItemTransactionResult>.BadRequest(
            ItemApiErrorCodes.SimulationOperationInvalid,
            message);
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
            ItemTransactionErrorCodes.SimulationSessionInvalid =>
                ServiceResult<ItemTransactionResult>.Unauthorized(error.Code, error.Message),
            ItemTransactionErrorCodes.AuthorityRequired or
            ItemTransactionErrorCodes.WrongSimulationWorker or
            ItemTransactionErrorCodes.BankAccessRequired or
            ItemTransactionErrorCodes.RecoveryAccessRequired =>
                ServiceResult<ItemTransactionResult>.Forbidden(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemNotFound or
            ItemTransactionErrorCodes.ItemNotOwned or
            ItemTransactionErrorCodes.RecoveryDeliveryNotFound or
            ItemTransactionErrorCodes.ItemPolicyNotFound =>
                ServiceResult<ItemTransactionResult>.NotFound(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemStateConflict or
            ItemTransactionErrorCodes.ItemOperationConflict or
            ItemTransactionErrorCodes.ItemSlotOccupied or
            ItemTransactionErrorCodes.EquipmentSlotOccupied or
            ItemTransactionErrorCodes.ItemQuantityChanged or
            ItemTransactionErrorCodes.BagStateChanged or
            ItemTransactionErrorCodes.WorkerRuntimeChanged =>
                ServiceResult<ItemTransactionResult>.Conflict(error.Code, error.Message),
            _ => ServiceResult<ItemTransactionResult>.UnprocessableEntity(
                error.Code,
                error.Message)
        };
    }
}
