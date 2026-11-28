using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Http;

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
    string? QuestCatalogRevision = null,
    // ServiceContract.Version of the worker; a heartbeat of another version, or of none, is refused.
    int? ServiceContractVersion = null);

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
    DateTime OnlineUntil,
    // The lease of every simulation session of this Shard, which the worker checks against its
    // own timing settings.
    int SessionLeaseSeconds);

internal sealed record SimulationWorkerOfflineRequest(string? RuntimeId);

internal sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

// The join of a character, with the versions of the client that asks: its realtime protocol
// version and simulation revision, which GameService compares with the Shard's worker before it
// issues a ticket, and, when the client has the baked collision data of the World that it believes
// the Shard runs, that World and its collision revision. A request without versions is refused as
// from a client that must update.
internal sealed record JoinShardRequest(
    Guid CharacterId,
    int? ProtocolVersion = null,
    string? SimulationRevision = null,
    string? WorldId = null,
    string? CollisionRevision = null);

// A join ticket: its public id, which travels over UDP in clear, and the transport key, which
// never does. The key travels only in this HTTPS response and, once the join proved it, in the
// consume response to the worker.
internal sealed record JoinShardResponse(
    ShardResponse Shard,
    SimulationEndpointResponse Endpoint,
    Guid CharacterId,
    Guid JoinTicketId,
    byte[] TransportKey,
    DateTime ExpiresAt,
    bool IsReconnect);

// The worker forwards the ticket id, the client's nonce, and its proof from the join request,
// with the protocol version of the request and the worker's own identity.
internal sealed record ConsumeSimulationJoinTicketRequest(
    Guid TicketId,
    byte[]? ClientNonce,
    byte[]? Proof,
    int ProtocolVersion,
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

    // How many times GameService issued the session's token: it grows with every reconnect's
    // consumption of a ticket, which rotates the token, so of two joins of one session the one with
    // the higher number consumed last, whatever order their answers arrive in. 0 when GameService
    // gives none, as for a development bot, and then the join is not ordered.
    public long SessionGeneration { get; init; }

    // What the character has equipped at ItemStateRevision.
    public IReadOnlyList<SimulationEquippedItem> Equipment { get; init; } = [];

    // How the character looks; null for a development bot, which the worker dresses itself.
    public SimulationAppearance? Appearance { get; init; }

    // The character's saved place and vitals, or null when it has none, as for a development bot.
    public SimulationSavedCharacterState? SavedState { get; init; }

    // The character's level and experience at the consume; null for a development bot.
    public SimulationProgression? Progression { get; init; }

    // The ticket's transport key, from which the worker derives the keys of the sealed datagrams.
    public byte[] TransportKey { get; init; } = [];
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

// A character's level, the experience it gathered inside that level, what the level needs, and
// the highest Mob level that gives it no experience: int.MaxValue at the highest level, where no
// Mob gives any.
internal sealed record SimulationProgression(
    int Level,
    long Experience,
    long ExperienceToNextLevel,
    int HighestMobLevelWithoutExperience)
{
    public static SimulationProgression From(CharacterProgressionSnapshot snapshot) => new(
        snapshot.Level,
        snapshot.Experience,
        snapshot.ExperienceToNextLevel,
        snapshot.HighestMobLevelWithoutExperience);
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

// The heartbeat of a worker runtime's simulation sessions: each session with the token that
// proves it, as the release proves it.
internal sealed record SimulationSessionHeartbeatRequest(
    string? RuntimeId,
    SimulationSessionHeartbeatEntry[]? Sessions);

internal sealed record SimulationSessionHeartbeatEntry(Guid SimulationSessionId, string? SessionToken);

// One answer per session of a heartbeat, in the request's order.
internal sealed record SimulationSessionHeartbeatResponse(SimulationSessionHeartbeatResult[] Sessions);

// The renewed lease of one session, or why it was not renewed: invalid_simulation_session for a
// session that is released, expired, not the worker runtime's, or proved by another token.
internal sealed record SimulationSessionHeartbeatResult(
    Guid SimulationSessionId,
    SimulationSessionLeaseResponse? Lease,
    ErrorResponse? Error)
{
    public static SimulationSessionHeartbeatResult Renewed(SimulationSessionLeaseResponse lease)
    {
        return new SimulationSessionHeartbeatResult(lease.SimulationSessionId, lease, null);
    }

    public static SimulationSessionHeartbeatResult Refused(Guid simulationSessionId, ErrorResponse error)
    {
        return new SimulationSessionHeartbeatResult(simulationSessionId, null, error);
    }
}

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

    // A heartbeat's code for how the session's account session ended, account_session_replaced or
    // account_session_ended, or null while it is valid. The session goes on: the worker ends the
    // connection and keeps the character's body.
    public string? AccountSessionEnded { get; init; }
}
