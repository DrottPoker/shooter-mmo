using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Economy;

// The vendor and gathering operations of Items' worker item route. A request that its kind cannot
// run gets the route's answer for an unsupported operation; a run command is answered by the
// route, its rejection mapped by SimulationEconomyRejections.

// The fields of a vendor or gathering request that the route does not know. Every economy kind
// reads all of them, so a field of the wrong type refuses any economy request.
internal sealed record EconomyOperationFields(
    string? DefinitionId = null,
    long? ExpectedPrice = null,
    string? ResourceProfileId = null,
    string? ResourceNodeId = null,
    string? RequiredToolKind = null,
    string? ResourceActorId = null);

// VendorProfileId is the vendor profile of the NPC whose vendor capability the worker validated
// for this operation.
internal sealed record EconomyAccessFields(string? VendorProfileId = null);

// The economy fields of an operation and its actor with the vendor capability claim, when the
// worker names a vendor profile.
internal sealed record EconomyOperationRequest(
    EconomyOperationFields Fields,
    string? VendorProfileId,
    ItemTransactionActor Actor)
{
    public static EconomyOperationRequest Read(SimulationItemOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var fields = operation.ReadFields<EconomyOperationFields>();
        var vendorProfileId = operation.ReadAccessFields<EconomyAccessFields>().VendorProfileId;
        return new EconomyOperationRequest(
            fields,
            vendorProfileId,
            VendorAccess.Claim(
                operation.Actor,
                string.IsNullOrWhiteSpace(vendorProfileId) ? null : vendorProfileId.Trim()));
    }
}

// The vendor is the profile of the capability the worker validated, never a client value.
internal sealed class VendorPurchaseOperationHandler : ISimulationItemOperationHandler
{
    public string OperationKind => EconomyOperationKinds.VendorPurchase;

    public Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
        SimulationItemOperation operation,
        CancellationToken cancellationToken)
    {
        var request = EconomyOperationRequest.Read(operation);
        var fields = request.Fields;
        return operation.HasInteraction
            && Identifiers.IsContentIdentifier(request.VendorProfileId)
            && Identifiers.IsContentIdentifier(fields.DefinitionId)
            && operation.Quantity > 0
            && fields.ExpectedPrice >= 0
            ? operation.RunAsync(
                request.Actor,
                new PurchaseFromVendorCommand(
                    operation.CharacterId,
                    operation.ExpectedCharacterRevision,
                    request.VendorProfileId.Trim(),
                    fields.DefinitionId.Trim(),
                    operation.Quantity!.Value,
                    fields.ExpectedPrice!.Value),
                SimulationEconomyRejections.Map<SimulationItemOperationResponse>,
                cancellationToken)
            : Task.FromResult(operation.Unsupported());
    }
}

internal sealed class VendorSaleOperationHandler : ISimulationItemOperationHandler
{
    public string OperationKind => EconomyOperationKinds.VendorSale;

    public Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
        SimulationItemOperation operation,
        CancellationToken cancellationToken)
    {
        var request = EconomyOperationRequest.Read(operation);
        var fields = request.Fields;
        return operation.HasItem
            && operation.HasInteraction
            && Identifiers.IsContentIdentifier(request.VendorProfileId)
            && operation.Quantity > 0
            && fields.ExpectedPrice >= 0
            ? operation.RunAsync(
                request.Actor,
                new SellToVendorCommand(
                    operation.CharacterId,
                    operation.ExpectedCharacterRevision,
                    request.VendorProfileId.Trim(),
                    operation.ItemInstanceId!.Value,
                    operation.ExpectedItemRevision!.Value,
                    operation.Quantity!.Value,
                    fields.ExpectedPrice!.Value),
                SimulationEconomyRejections.Map<SimulationItemOperationResponse>,
                cancellationToken)
            : Task.FromResult(operation.Unsupported());
    }
}

// The worker resolved the yield and the tool kind from the node's resource profile and names the
// node's actor definition for gather objectives; the command checks the equipped tool and the
// item rules.
internal sealed class GatherResourceOperationHandler : ISimulationItemOperationHandler
{
    public string OperationKind => EconomyOperationKinds.GatherResource;

    public Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
        SimulationItemOperation operation,
        CancellationToken cancellationToken)
    {
        var request = EconomyOperationRequest.Read(operation);
        var fields = request.Fields;
        return operation.HasInteraction
            && Identifiers.IsContentIdentifier(fields.ResourceProfileId)
            && !string.IsNullOrWhiteSpace(fields.ResourceNodeId)
            && Identifiers.IsContentIdentifier(fields.RequiredToolKind)
            && Identifiers.IsContentIdentifier(fields.DefinitionId)
            && operation.Quantity > 0
            && (fields.ResourceActorId is null || Identifiers.IsContentIdentifier(fields.ResourceActorId))
            ? operation.RunAsync(
                request.Actor,
                new GatherResourceCommand(
                    operation.CharacterId,
                    fields.ResourceProfileId.Trim(),
                    fields.ResourceNodeId.Trim(),
                    fields.RequiredToolKind.Trim(),
                    fields.DefinitionId.Trim(),
                    operation.Quantity!.Value,
                    fields.ResourceActorId?.Trim()),
                SimulationEconomyRejections.Map<SimulationItemOperationResponse>,
                cancellationToken)
            : Task.FromResult(operation.Unsupported());
    }
}
