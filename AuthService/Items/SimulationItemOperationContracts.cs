namespace AuthService.Items;

public sealed record SimulationItemAccessRequest(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc);

public sealed record SimulationItemOperationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string? WorkerId,
    string? WorkerRuntimeId,
    string? ShardId,
    string? SessionToken,
    SimulationItemAccessRequest? Access,
    string? OperationKind,
    long ExpectedCharacterRevision,
    Guid? ItemInstanceId = null,
    long? ExpectedItemRevision = null,
    Guid? TargetItemInstanceId = null,
    long? ExpectedTargetItemRevision = null,
    Guid? DestinationContainerId = null,
    int? DestinationSlotIndex = null,
    int? Quantity = null,
    string? EquipmentSlotId = null,
    Guid? RecoveryDeliveryId = null,
    long? ExpectedRecoveryDeliveryRevision = null,
    IReadOnlyList<ItemRevisionExpectation>? Items = null);
