using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items;

internal sealed class SimulationItemMutationService(
    ItemTransactionService transactionService,
    SimulationCharacterStates characterStates,
    NpcItemLifecycleOptions npcOptions)
{
    public const int MaximumRecoveryClaimItems = 24;

    public async Task<ServiceResult<SimulationItemOperationResponse>> ConsumeAmmunitionAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationAmmunitionConsumptionRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(authenticatedWorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return ServiceResult<SimulationItemOperationResponse>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot consume another worker's ammunition.");
        }

        if (simulationSessionId == Guid.Empty
            || request.OperationId == Guid.Empty
            || request.AccountId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || request.ExpectedCharacterRevision < 0
            || request.ItemInstanceId == Guid.Empty
            || request.ExpectedItemRevision < 0
            || request.Quantity is < 1 or > ItemCatalogFormat.MaximumQuantity
            || !Identifiers.IsServiceIdentifier(request.WorkerId)
            || !Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            || !Identifiers.IsServiceIdentifier(request.ShardId)
            || string.IsNullOrWhiteSpace(request.SessionToken)
            || request.SessionToken.Length > 1024)
        {
            return Invalid("The ammunition consumption request is incomplete or invalid.");
        }

        var actor = ItemTransactionActor.ForSimulationWorker(
            request.AccountId,
            request.CharacterId,
            simulationSessionId,
            request.WorkerId!.Trim(),
            request.WorkerRuntimeId!.Trim(),
            request.ShardId!.Trim(),
            TokenGenerator.HashToken(request.SessionToken!.Trim()),
            ItemTransactionLiveAccess.None);
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<ConsumeEquippedAmmunitionCommand>(
                request.OperationId,
                actor,
                new ConsumeEquippedAmmunitionCommand(
                    request.CharacterId,
                    request.ExpectedCharacterRevision,
                    request.ItemInstanceId,
                    request.ExpectedItemRevision,
                    request.Quantity)),
            cancellationToken);
        return await MapResultAsync(result, request.CharacterId, cancellationToken);
    }

    public async Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
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
            return ServiceResult<SimulationItemOperationResponse>.Forbidden(
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
                request.Access.InsuranceNpc,
                request.Access.QuestNpc,
                string.IsNullOrWhiteSpace(request.Access.VendorProfileId)
                    ? null
                    : request.Access.VendorProfileId.Trim()));

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
            ItemOperationKinds.ApplyItemPolicy => ExecuteInsuranceApplyAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.RemoveInsurancePolicy => ExecuteInsuranceRemovalAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.Grant => ExecuteQuestGrantAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.AbandonQuestItems => ExecuteQuestAbandonAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.VendorPurchase => ExecuteVendorPurchaseAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.VendorSale => ExecuteVendorSaleAsync(
                actor,
                request,
                cancellationToken),
            ItemOperationKinds.GatherResource => ExecuteGatherAsync(
                actor,
                request,
                cancellationToken),
            _ => null
        };

        return operation is null
            ? Invalid("The requested in-world item operation is not supported.")
            : await MapResultAsync(await operation, request.CharacterId, cancellationToken);
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

    private Task<ItemTransactionResult>? ExecuteInsuranceApplyAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request)
            && HasNpcContext(request)
            && request.Access!.InsuranceNpc
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<ApplyItemPolicyCommand>(
                    request.OperationId,
                    actor,
                    new ApplyItemPolicyCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        ItemPolicyIds.Insured,
                        ItemPolicySourceKinds.InsuranceService,
                        CreateInsuranceGrantId(request),
                        npcOptions.InsurancePrice)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteInsuranceRemovalAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request)
            && HasNpcContext(request)
            && request.Access!.InsuranceNpc
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<RemoveInsurancePolicyCommand>(
                    request.OperationId,
                    actor,
                    new RemoveInsurancePolicyCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteQuestGrantAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasNpcContext(request)
            && request.Access!.QuestNpc
            && request.DestinationContainerId is not null
            && request.DestinationContainerId != Guid.Empty
            && request.DestinationSlotIndex is null or >= 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    request.OperationId,
                    actor,
                    new GrantItemCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        npcOptions.QuestItemDefinitionId,
                        npcOptions.QuestItemQuantity,
                        request.DestinationContainerId.Value,
                        request.DestinationSlotIndex,
                        ItemPolicySourceKinds.QuestGrant,
                        npcOptions.QuestGrantId)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteQuestAbandonAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasNpcContext(request)
            && request.Access!.QuestNpc
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<AbandonQuestItemsCommand>(
                    request.OperationId,
                    actor,
                    new AbandonQuestItemsCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        npcOptions.QuestGrantId)),
                cancellationToken)
            : null;
    }

    // The vendor is the profile of the capability the worker validated, never a client value.
    private Task<ItemTransactionResult>? ExecuteVendorPurchaseAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasNpcContext(request)
            && Identifiers.IsContentIdentifier(request.Access!.VendorProfileId)
            && Identifiers.IsContentIdentifier(request.DefinitionId)
            && request.Quantity > 0
            && request.ExpectedPrice >= 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<PurchaseFromVendorCommand>(
                    request.OperationId,
                    actor,
                    new PurchaseFromVendorCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.Access.VendorProfileId!.Trim(),
                        request.DefinitionId!.Trim(),
                        request.Quantity!.Value,
                        request.ExpectedPrice!.Value)),
                cancellationToken)
            : null;
    }

    private Task<ItemTransactionResult>? ExecuteVendorSaleAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasItem(request)
            && HasNpcContext(request)
            && Identifiers.IsContentIdentifier(request.Access!.VendorProfileId)
            && request.Quantity > 0
            && request.ExpectedPrice >= 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<SellToVendorCommand>(
                    request.OperationId,
                    actor,
                    new SellToVendorCommand(
                        request.CharacterId,
                        request.ExpectedCharacterRevision,
                        request.Access.VendorProfileId!.Trim(),
                        request.ItemInstanceId!.Value,
                        request.ExpectedItemRevision!.Value,
                        request.Quantity!.Value,
                        request.ExpectedPrice!.Value)),
                cancellationToken)
            : null;
    }

    // The worker resolved the yield and the tool kind from the node's resource profile;
    // the handler checks the equipped tool and the item rules.
    private Task<ItemTransactionResult>? ExecuteGatherAsync(
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return HasNpcContext(request)
            && Identifiers.IsContentIdentifier(request.ResourceProfileId)
            && !string.IsNullOrWhiteSpace(request.ResourceNodeId)
            && Identifiers.IsContentIdentifier(request.RequiredToolKind)
            && Identifiers.IsContentIdentifier(request.DefinitionId)
            && request.Quantity > 0
            ? transactionService.ExecuteAsync(
                new ItemTransactionRequest<GatherResourceCommand>(
                    request.OperationId,
                    actor,
                    new GatherResourceCommand(
                        request.CharacterId,
                        request.ResourceProfileId!.Trim(),
                        request.ResourceNodeId!.Trim(),
                        request.RequiredToolKind!.Trim(),
                        request.DefinitionId!.Trim(),
                        request.Quantity!.Value)),
                cancellationToken)
            : null;
    }

    private static ServiceResult<SimulationItemOperationResponse>? ValidateCommon(
        Guid simulationSessionId,
        SimulationItemOperationRequest request)
    {
        if (simulationSessionId == Guid.Empty
            || request.OperationId == Guid.Empty
            || request.AccountId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || request.ExpectedCharacterRevision < 0
            || !Identifiers.IsServiceIdentifier(request.WorkerId)
            || !Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            || !Identifiers.IsServiceIdentifier(request.ShardId)
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

    // The worker validated the interaction and the capability kind, which its access flags
    // carry. The capability id is content, so only its presence is checked here.
    private static bool HasNpcContext(SimulationItemOperationRequest request)
    {
        return request.InteractionSessionId is not null
            && request.InteractionSessionId != Guid.Empty
            && !string.IsNullOrWhiteSpace(request.CapabilityId);
    }

    private static string CreateInsuranceGrantId(SimulationItemOperationRequest request)
    {
        return "npc:" + request.InteractionSessionId!.Value.ToString("N")
            + ":" + request.OperationId.ToString("N");
    }


    private static ServiceResult<SimulationItemOperationResponse> Invalid(string message)
    {
        return ServiceResult<SimulationItemOperationResponse>.BadRequest(
            ItemApiErrorCodes.SimulationOperationInvalid,
            message);
    }

    private async Task<ServiceResult<SimulationItemOperationResponse>> MapResultAsync(
        ItemTransactionResult result,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return result.Succeeded
            ? ServiceResult<SimulationItemOperationResponse>.Ok(
                SimulationItemOperationResponse.From(
                    result,
                    await characterStates.ReadAsync(characterId, cancellationToken)))
            : MapRejection(result);
    }

    private static ServiceResult<SimulationItemOperationResponse> MapRejection(
        ItemTransactionResult result)
    {
        var error = result.Error
            ?? throw new InvalidOperationException("A rejected item operation has no error.");
        return error.Code switch
        {
            ItemTransactionErrorCodes.SimulationSessionInvalid =>
                ServiceResult<SimulationItemOperationResponse>.Unauthorized(error.Code, error.Message),
            ItemTransactionErrorCodes.AuthorityRequired or
            ItemTransactionErrorCodes.WrongSimulationWorker or
            ItemTransactionErrorCodes.BankAccessRequired or
            ItemTransactionErrorCodes.RecoveryAccessRequired or
            ItemTransactionErrorCodes.VendorAccessRequired =>
                ServiceResult<SimulationItemOperationResponse>.Forbidden(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemNotFound or
            ItemTransactionErrorCodes.ItemNotOwned or
            ItemTransactionErrorCodes.RecoveryDeliveryNotFound or
            ItemTransactionErrorCodes.ItemPolicyNotFound =>
                ServiceResult<SimulationItemOperationResponse>.NotFound(error.Code, error.Message),
            ItemTransactionErrorCodes.ItemStateConflict or
            ItemTransactionErrorCodes.ItemOperationConflict or
            ItemTransactionErrorCodes.ItemTransactionTimeout or
            ItemTransactionErrorCodes.ItemSlotOccupied or
            ItemTransactionErrorCodes.EquipmentSlotOccupied or
            ItemTransactionErrorCodes.ItemQuantityChanged or
            ItemTransactionErrorCodes.BagStateChanged or
            ItemTransactionErrorCodes.WorkerRuntimeChanged or
            ItemTransactionErrorCodes.VendorPriceChanged or
            ItemTransactionErrorCodes.InventoryFull =>
                ServiceResult<SimulationItemOperationResponse>.Conflict(error.Code, error.Message),
            _ => ServiceResult<SimulationItemOperationResponse>.UnprocessableEntity(
                error.Code,
                error.Message)
        };
    }
}
