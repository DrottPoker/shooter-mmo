namespace Mmorpg.GameService.Items;

internal static class ItemApiErrorCodes
{
    public const string OperationIdRequired = "item_operation_id_required";
    public const string SimulationOperationInvalid = "item_simulation_operation_invalid";
    public const string SecureTierRequestInvalid = "item_secure_tier_request_invalid";
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

internal sealed record ChangeAccountSecureContainerTierRequest(
    Guid OperationId,
    long ExpectedEntitlementRevision,
    string? TierId,
    IReadOnlyList<CharacterRevisionExpectation>? CharacterRevisions);
