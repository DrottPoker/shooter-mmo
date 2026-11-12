using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;

namespace Mmorpg.GameService.Placement;

internal sealed record ShardResponse(
    string Id,
    string DisplayName,
    string WorldId,
    string FleetId,
    string FleetDisplayName,
    string RegionCode,
    string RuleSet,
    bool IsOnline,
    int ActivePlayers,
    int Capacity);

internal sealed record SimulationEndpointResponse(
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

internal sealed record SimulationWorkerHeartbeatRequest(
    string? FleetId,
    string? NodeId,
    string? ShardId,
    string? WorldId,
    string? RuntimeId,
    DateTime StartedAt,
    string? Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string? SimulationRevision,
    string? CollisionRevision,
    string? ItemCatalogRevision,
    string? QuestCatalogRevision = null);

internal sealed record SimulationWorkerHeartbeatResponse(
    string WorkerId,
    string RuntimeId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string RuleSet,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision,
    string ItemCatalogRevision,
    string QuestCatalogRevision,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

internal sealed record SimulationWorkerOfflineRequest(string? RuntimeId);

internal sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

internal sealed record JoinShardRequest(Guid CharacterId);

internal sealed record JoinShardResponse(
    ShardResponse Shard,
    SimulationEndpointResponse Endpoint,
    Guid CharacterId,
    string JoinTicket,
    DateTime ExpiresAt,
    bool IsReconnect);

internal sealed record ConsumeSimulationJoinTicketRequest(
    string? Ticket,
    string? WorkerId,
    string? RuntimeId,
    string? ShardId);

internal sealed record ConsumedSimulationJoinTicketResponse(
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

    // The lease duration lets the worker set a deadline on its own clock, so clock
    // differences between the worker and the database never move the deadline.
    public long SessionLeaseDurationMilliseconds { get; init; }

    // What the character has equipped at ItemStateRevision.
    public IReadOnlyList<SimulationEquippedItem> Equipment { get; init; } = [];

    // How the character looks; null for a development bot, which the worker dresses itself.
    public SimulationAppearance? Appearance { get; init; }

    // The character's saved place and vitals, or null when it has none, as for a development bot.
    public SimulationSavedCharacterState? SavedState { get; init; }
}

// A character's saved place and vitals: the World and its collision revision when the state was
// saved, the position and facing as the simulation's floats, and health and mana.
internal sealed record SimulationSavedCharacterState(
    string WorldId,
    string CollisionRevision,
    float PositionX,
    float PositionY,
    float PositionZ,
    float YawDegrees,
    int Health,
    int Mana)
{
    public static SimulationSavedCharacterState From(CharacterSimulationState state) => new(
        state.WorldId,
        state.CollisionRevision,
        state.PositionX,
        state.PositionY,
        state.PositionZ,
        state.YawDegrees,
        state.Health,
        state.Mana);
}

// A character's place and vitals as the worker sends them, numbered within its simulation
// session.
internal sealed record SimulationCharacterStateRequest(
    long Sequence,
    float PositionX,
    float PositionY,
    float PositionZ,
    float YawDegrees,
    int Health,
    int Mana);

// The release of a session, with the final state of its character when the worker has one.
internal sealed record SimulationSessionReleaseRequest(
    string? WorkerRuntimeId,
    string? SessionToken,
    SimulationCharacterStateRequest? FinalState = null)
{
    public SimulationSessionCredentialRequest Credentials => new(WorkerRuntimeId, SessionToken);
}

// One character state of a checkpoint: the simulation session that has it, the sequence within
// that session, and the state.
internal sealed record SimulationCharacterCheckpointEntry(
    Guid SimulationSessionId,
    long Sequence,
    float PositionX,
    float PositionY,
    float PositionZ,
    float YawDegrees,
    int Health,
    int Mana)
{
    public SimulationCharacterStateRequest State => new(
        Sequence,
        PositionX,
        PositionY,
        PositionZ,
        YawDegrees,
        Health,
        Mana);
}

// The character states of one worker runtime's sessions that changed since its last checkpoint.
internal sealed record SimulationCharacterCheckpointRequest(
    string? RuntimeId,
    SimulationCharacterCheckpointEntry[]? States);

// How many states a checkpoint saved, and how many it skipped because their session was not the
// character's active session on the worker runtime or a newer state of that session was stored.
internal sealed record SimulationCharacterCheckpointResponse(int Accepted, int Skipped);

// A character's look as the worker sends it to every client: its body model, the choices of its
// look, and whether it lowered its headwear.
internal sealed record SimulationAppearance(string BodyModel, SimulationAppearanceChoice[] Choices, bool HeadwearLowered)
{
    public static SimulationAppearance From(CharacterAppearanceState state) => new(
        state.BodyModel,
        state.Choices.Select(choice => new SimulationAppearanceChoice(choice.Kind, choice.Slot, choice.Option)).ToArray(),
        state.HeadwearLowered);
}

internal sealed record SimulationAppearanceChoice(string Kind, string Slot, string Option);

// A change of the look of a session's character: lowering or raising its headwear, or in
// Development another body model and look.
internal sealed record SimulationAppearanceChangeRequest(
    string? WorkerRuntimeId,
    string? SessionToken,
    string? Change,
    bool? HeadwearLowered,
    string? BodyModel,
    SimulationAppearanceChoice[]? Choices);

internal sealed record SimulationSessionCredentialRequest(
    string? WorkerRuntimeId,
    string? SessionToken);

internal sealed record SimulationSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool Released)
{
    // The lease duration lets the worker set a deadline on its own clock, so clock
    // differences between the worker and the database never move the deadline.
    public long LeaseDurationMilliseconds { get; init; }

    // What the character has equipped at ItemStateRevision.
    public IReadOnlyList<SimulationEquippedItem> Equipment { get; init; } = [];

    // Whether a release stored the final state it carried.
    public bool FinalStateSaved { get; init; }
}
