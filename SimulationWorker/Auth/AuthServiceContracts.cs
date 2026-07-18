using System.Net;

namespace SimulationWorker.Auth;

public sealed record ConsumeSimulationJoinTicketRequest(
    string Ticket,
    string WorkerId,
    string RuntimeId,
    string ShardId);

public sealed record ConsumedSimulationJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    string WorkerId,
    string WorkerRuntimeId,
    Guid SimulationSessionId,
    string SimulationSessionToken,
    DateTime SessionExpiresAt,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }
}

public sealed record SimulationSessionCredentialRequest(
    string WorkerRuntimeId,
    string SessionToken);

public sealed record SimulationSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool Released);

public sealed record SimulationItemAccessRequest(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc,
    bool QuestNpc = false);

public sealed record SimulationItemRevisionExpectation(
    Guid ItemInstanceId,
    long Revision);

public sealed record SimulationItemOperationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    SimulationItemAccessRequest Access,
    string OperationKind,
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
    IReadOnlyList<SimulationItemRevisionExpectation>? Items = null,
    Guid? InteractionSessionId = null,
    string? CapabilityId = null);

public sealed record SimulationPlayerDeathRequest(
    Guid OperationId,
    Guid DeathEventId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    long ExpectedCharacterRevision,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey);

public sealed record PlayerDeathPartitionResponse(
    Guid OperationId,
    Guid DeathEventId,
    DurableCorpseResponse Corpse,
    SimulationItemCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds);

public sealed record MobCorpseLootEntryRequest(
    Guid GrantId,
    string DefinitionId,
    int Quantity);

public sealed record CreatePersistentMobCorpseRequest(
    Guid OperationId,
    Guid CorpseId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SourceActorDefinitionId,
    string SourceDisplayName,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    double LifetimeSeconds,
    IReadOnlyList<MobCorpseLootEntryRequest> Loot);

public sealed record PersistentMobCorpseResponse(
    Guid OperationId,
    DurableCorpseResponse Corpse);

public sealed record SimulationMobLootGrantRequest(
    Guid GrantId,
    Guid AccountId,
    Guid CharacterId,
    long ExpectedCharacterRevision,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    Guid SourceCorpseId,
    string SourceActorDefinitionId,
    string DefinitionId,
    int Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex);

public sealed record SimulationItemTransactionResponse(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    SimulationItemTransactionError? Error,
    IReadOnlyList<SimulationItemCharacterRevision> CharacterRevisions,
    IReadOnlyList<SimulationItemContainerRevision> ContainerRevisions,
    IReadOnlyList<SimulationItemInstanceRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    long? SecureContainerEntitlementRevision);

public sealed record SimulationItemTransactionError(string Code, string Message);

public sealed record SimulationItemCharacterRevision(
    Guid CharacterId,
    long Revision,
    long CarriedWeight,
    long CarryCapacity);

public sealed record SimulationItemContainerRevision(Guid ContainerId, long Revision);

public sealed record SimulationItemInstanceRevision(Guid ItemInstanceId, long Revision);

public sealed record SimulationWorkerHeartbeatResponse(
    string WorkerId,
    string RuntimeId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

public sealed record SimulationWorkerHeartbeatRequest(
    string FleetId,
    string NodeId,
    string ShardId,
    string RuntimeId,
    DateTime StartedAt,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record SimulationWorkerOfflineRequest(string RuntimeId);

public sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

public sealed record CorpseRestoreResponse(
    DateTime DatabaseTime,
    IReadOnlyList<DurableCorpseResponse> Corpses);

public sealed record DurableCorpseResponse(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string SourceDisplayName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool IsEmpty,
    IReadOnlyList<CorpseSectionResponse> Sections);

public sealed record CorpseSectionResponse(
    string SectionKind,
    Guid ContainerId,
    long ContainerRevision,
    int ItemCount);

public sealed record SimulationCorpseOpenRequest(
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken);

public sealed record SimulationCorpseMutationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    string OperationKind,
    long ExpectedCorpseRevision,
    Guid? ItemInstanceId = null,
    long? ExpectedItemRevision = null,
    int? Quantity = null,
    Guid? DestinationContainerId = null,
    long? ExpectedDestinationContainerRevision = null,
    int? DestinationSlotIndex = null,
    Guid? TargetItemInstanceId = null,
    long? ExpectedTargetItemRevision = null,
    Guid? CorpseBagContentsContainerId = null,
    long? ExpectedCorpseBagContentsRevision = null,
    Guid? PlayerBagItemInstanceId = null,
    long? ExpectedPlayerBagRevision = null,
    Guid? PlayerBagContentsContainerId = null,
    long? ExpectedPlayerBagContentsRevision = null);

public sealed record CorpseMutationResponse(
    SimulationItemTransactionResponse Transaction,
    CorpseViewSnapshotResponse? Corpse);

public sealed record CorpseViewSnapshotResponse(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string SourceDisplayName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    string PresentationKey,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    IReadOnlyList<CorpseViewSectionResponse> Sections,
    IReadOnlyList<CorpsePresentationSnapshotResponse> PresentationSnapshots);

public sealed record CorpseViewSectionResponse(
    string SectionKind,
    Guid ContainerId,
    string ContainerType,
    long ContainerRevision,
    int SlotCapacity,
    IReadOnlyList<CorpseViewSlotResponse> Slots);

public sealed record CorpseViewSlotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags,
    CorpseViewItemResponse? Item,
    string EquipmentSlotId = "");

public sealed record CorpseViewItemResponse(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision,
    Guid? BagContentsContainerId,
    long? BagContentsRevision);

public sealed record CorpsePresentationSnapshotResponse(
    string SnapshotKind,
    int SortOrder,
    string? DefinitionId,
    string? EquipmentSlotId,
    string? PolicyKind,
    string PresentationPayload);

public sealed record AuthServiceProblemDetails(string? Code, string? Detail, string? Message);

public sealed record AuthServiceResult<T>(T? Value, SimulationWorkerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static AuthServiceResult<T> Success(T value)
    {
        return new AuthServiceResult<T>(value, null, (int)HttpStatusCode.OK);
    }

    public static AuthServiceResult<T> Failure(int statusCode, string code, string message)
    {
        return new AuthServiceResult<T>(
            default,
            new SimulationWorkerErrorResponse(code, message),
            statusCode);
    }
}

public sealed record SimulationWorkerErrorResponse(string Code, string Message);
