using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmorpg.GameService.Items;

// Fields holds the access fields that only the handler of an operation kind knows, as the worker
// sent them, such as the vendor profile of the NPC whose vendor capability the worker validated.
internal sealed record SimulationItemAccessRequest(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Fields { get; init; }
}

// Fields holds the request fields that only the handler of an operation kind knows, as the
// worker sent them; ISimulationItemOperationHandler reads them.
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
    string? CapabilityId = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Fields { get; init; }
}

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
