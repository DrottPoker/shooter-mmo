using Mmorpg.GameService.Items.Contracts;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items;

// The vendor and gathering operations of the worker item route.
internal sealed partial class SimulationItemMutationService
{
    // The vendor profile of the NPC whose vendor capability the worker validated, when it names
    // one.
    private static ItemTransactionLiveAccess ClaimEconomyCapabilities(
        ItemTransactionLiveAccess access,
        SimulationItemAccessRequest request)
    {
        return VendorAccess.Claim(
            access,
            string.IsNullOrWhiteSpace(request.VendorProfileId) ? null : request.VendorProfileId.Trim());
    }

    // The vendor or gathering operation of the kind, or null for another kind.
    private Task<ItemTransactionResult>? ExecuteEconomyOperation(
        string operationKind,
        ItemTransactionActor actor,
        SimulationItemOperationRequest request,
        CancellationToken cancellationToken)
    {
        return operationKind switch
        {
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

    // The worker resolved the yield and the tool kind from the node's resource profile and
    // names the node's actor definition for gather objectives; the handler checks the
    // equipped tool and the item rules.
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
            && (request.ResourceActorId is null || Identifiers.IsContentIdentifier(request.ResourceActorId))
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
                        request.Quantity!.Value,
                        request.ResourceActorId?.Trim())),
                cancellationToken)
            : null;
    }
}
