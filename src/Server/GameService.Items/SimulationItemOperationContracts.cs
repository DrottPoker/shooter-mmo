using Mmorpg.GameService.Placement.Contracts;

namespace Mmorpg.GameService.Items;

internal sealed record SimulationItemAccessRequest(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc,
    bool QuestNpc = false);

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
    string? CapabilityId = null);

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

// The acting character's carry state and equipment, read after the operation committed.
// The worker applies it when its item-state revision is newer than what it knows.
internal sealed record SimulationCharacterStateResponse(
    Guid CharacterId,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    IReadOnlyList<SimulationEquippedItem> Equipment);

// A committed item transaction together with the acting character's state afterwards.
internal sealed record SimulationItemOperationResponse(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    ItemTransactionError? Error,
    IReadOnlyList<ItemTransactionCharacterRevision> CharacterRevisions,
    IReadOnlyList<ItemTransactionContainerRevision> ContainerRevisions,
    IReadOnlyList<ItemTransactionItemRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    long? SecureContainerEntitlementRevision,
    SimulationCharacterStateResponse? CharacterState)
{
    public static SimulationItemOperationResponse From(
        ItemTransactionResult result,
        SimulationCharacterStateResponse? characterState)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SimulationItemOperationResponse(
            result.OperationId,
            result.OperationKind,
            result.Succeeded,
            result.Error,
            result.CharacterRevisions,
            result.ContainerRevisions,
            result.ItemRevisions,
            result.RecoveryDeliveryIds,
            result.SecureContainerEntitlementRevision,
            characterState);
    }
}
