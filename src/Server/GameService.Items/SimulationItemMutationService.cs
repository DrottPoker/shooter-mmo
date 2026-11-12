using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Items;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Mmorpg.GameService.Items;

// The worker item route. It runs Items' own operation kinds and hands every other kind to the
// ISimulationItemOperationHandler of that kind, which a module above Items registers.
internal sealed class SimulationItemMutationService(
    ItemTransactionService transactionService,
    SimulationCharacterStates characterStates,
    NpcItemLifecycleOptions npcOptions,
    SimulationItemOperationHandlers handlers,
    IOptions<HttpJsonOptions> jsonOptions)
{
    public const int MaximumRecoveryClaimItems = 24;

    private const string NotSupportedMessage = "The requested in-world item operation is not supported.";

    // The kinds that the route runs itself. Each returns null for a request that its kind cannot
    // run.
    private static readonly FrozenDictionary<string, OwnOperation> OwnOperations =
        new Dictionary<string, OwnOperation>(StringComparer.Ordinal)
        {
            [ItemOperationKinds.Relocate] = static (route, actor, request, token) =>
                route.ExecuteRelocateAsync(actor, request, token),
            [ItemOperationKinds.Equip] = static (route, actor, request, token) =>
                route.ExecuteEquipAsync(actor, request, token),
            [ItemOperationKinds.Unequip] = static (route, actor, request, token) =>
                route.ExecuteUnequipAsync(actor, request, token),
            [ItemOperationKinds.SplitStack] = static (route, actor, request, token) =>
                route.ExecuteSplitAsync(actor, request, token),
            [ItemOperationKinds.MergeStacks] = static (route, actor, request, token) =>
                route.ExecuteMergeAsync(actor, request, token),
            [ItemOperationKinds.SwapContainerItems] = static (route, actor, request, token) =>
                route.ExecuteSwapAsync(actor, request, token),
            [ItemOperationKinds.Destroy] = static (route, actor, request, token) =>
                route.ExecuteDestroyAsync(actor, request, token),
            [ItemOperationKinds.ClaimRecoveryDelivery] = static (route, actor, request, token) =>
                route.ExecuteRecoveryClaimAsync(actor, request, token),
            [ItemOperationKinds.ApplyItemPolicy] = static (route, actor, request, token) =>
                route.ExecuteInsuranceApplyAsync(actor, request, token),
            [ItemOperationKinds.RemoveInsurancePolicy] = static (route, actor, request, token) =>
                route.ExecuteInsuranceRemovalAsync(actor, request, token)
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private delegate Task<ItemTransactionResult>? OwnOperation(
        SimulationItemMutationService route,
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken);

    public static IReadOnlyCollection<string> OwnKinds => OwnOperations.Keys;

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
                request.Access.InsuranceNpc));

        var operationKind = request.OperationKind!.Trim();
        if (!OwnOperations.TryGetValue(operationKind, out var own))
        {
            var handler = handlers.Find(operationKind);
            return handler is null
                ? Unsupported()
                : await handler.ExecuteAsync(
                    new SimulationItemOperation(
                        this,
                        simulationSessionId,
                        authenticatedWorkerId,
                        operationKind,
                        request,
                        actor,
                        jsonOptions.Value.SerializerOptions),
                    cancellationToken);
        }

        var operation = own(this, actor, request, cancellationToken);
        return operation is null
            ? Unsupported()
            : await MapResultAsync(await operation, request.CharacterId, cancellationToken);
    }

    // Runs a handler's command and answers it as the route answers its own kinds.
    internal async Task<ServiceResult<SimulationItemOperationResponse>> RunAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        Guid characterId,
        Func<ItemTransactionResult, ServiceResult<SimulationItemOperationResponse>> mapRejection,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        var result = await transactionService.ExecuteAsync(request, cancellationToken);
        return result.Succeeded
            ? await CommittedAsync(result, characterId, cancellationToken)
            : mapRejection(result);
    }

    internal static ServiceResult<SimulationItemOperationResponse> Unsupported()
    {
        return Invalid(NotSupportedMessage);
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
                        request.EquipmentSlotId.Trim(),
                        request.Quantity)),
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
                        request.ExpectedTargetItemRevision!.Value,
                        request.Quantity)),
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

    internal static bool HasItem(SimulationItemOperationRequest request)
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
    internal static bool HasNpcContext(SimulationItemOperationRequest request)
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
            ? await CommittedAsync(result, characterId, cancellationToken)
            : MapRejection(result);
    }

    private async Task<ServiceResult<SimulationItemOperationResponse>> CommittedAsync(
        ItemTransactionResult result,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return ServiceResult<SimulationItemOperationResponse>.Ok(
            SimulationItemOperationResponse.From(
                result,
                await characterStates.ReadAsync(characterId, cancellationToken)));
    }

    private static ServiceResult<SimulationItemOperationResponse> MapRejection(
        ItemTransactionResult result)
    {
        return SimulationItemRejections.Map<SimulationItemOperationResponse>(result);
    }
}
