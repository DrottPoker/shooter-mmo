namespace Mmorpg.GameService.Items;

internal static class ItemApiErrorCodes
{
    public const string OperationIdRequired = "item_operation_id_required";
    // The kernel rejects invalid corpse transfer commands with the same code.
    public const string SimulationOperationInvalid = ItemTransactionErrorCodes.SimulationOperationInvalid;
}

internal sealed record RelocateAccountItemRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

internal sealed record SplitAccountItemStackRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

internal sealed record MergeAccountItemStacksRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid SourceItemInstanceId,
    long ExpectedSourceItemRevision,
    Guid TargetItemInstanceId,
    long ExpectedTargetItemRevision);

internal sealed record DestroyAccountItemRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision);
