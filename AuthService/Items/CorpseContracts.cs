namespace AuthService.Items;

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
    ItemTransactionCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds);

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

internal sealed record CorpsePartitionRecord(
    Guid DeathEventId,
    Guid OriginalOperationId,
    Guid CharacterId,
    DurableCorpseResponse Corpse,
    ItemTransactionCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds);
