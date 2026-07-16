namespace AuthService.Items;

public static class ItemApiErrorCodes
{
    public const string OperationIdRequired = "item_operation_id_required";
    public const string SimulationOperationInvalid = "item_simulation_operation_invalid";
    public const string RecoveryClaimItemsRequired = "item_recovery_claim_items_required";
    public const string SecureTierRequestInvalid = "item_secure_tier_request_invalid";
}

public sealed record RelocateAccountItemRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

public sealed record SplitAccountItemStackRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

public sealed record MergeAccountItemStacksRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid SourceItemInstanceId,
    long ExpectedSourceItemRevision,
    Guid TargetItemInstanceId,
    long ExpectedTargetItemRevision);

public sealed record DestroyAccountItemRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision);

public sealed record ClaimAccountRecoveryDeliveryRequest(
    Guid OperationId,
    long ExpectedCharacterRevision,
    long ExpectedRecoveryDeliveryRevision,
    Guid DestinationContainerId,
    IReadOnlyList<ItemRevisionExpectation>? Items);

public sealed record ChangeAccountSecureContainerTierRequest(
    Guid OperationId,
    long ExpectedEntitlementRevision,
    string? TierId,
    IReadOnlyList<CharacterRevisionExpectation>? CharacterRevisions);
