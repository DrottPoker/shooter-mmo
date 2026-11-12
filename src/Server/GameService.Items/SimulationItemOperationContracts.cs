
namespace Mmorpg.GameService.Items;

// VendorProfileId is the vendor profile of the NPC whose vendor capability the worker
// validated for this operation.
internal sealed record SimulationItemAccessRequest(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc,
    string? VendorProfileId = null);

internal sealed record SimulationItemOperationRequest(
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
    IReadOnlyList<ItemRevisionExpectation>? Items = null,
    Guid? InteractionSessionId = null,
    string? CapabilityId = null,
    string? DefinitionId = null,
    long? ExpectedPrice = null,
    string? ResourceProfileId = null,
    string? ResourceNodeId = null,
    string? RequiredToolKind = null,
    string? ResourceActorId = null);

// One batch of shots from the character's equipped ammunition stack. The operation id is
// the worker's batch id, so a retry after a lost response replays the stored result.
internal sealed record SimulationAmmunitionConsumptionRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string? WorkerId,
    string? WorkerRuntimeId,
    string? ShardId,
    string? SessionToken,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);
