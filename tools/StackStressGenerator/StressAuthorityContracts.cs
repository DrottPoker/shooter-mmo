namespace Mmorpg.Tools.StackStressGenerator;

public sealed record StressWorkerHeartbeatRequest(
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
    string CollisionRevision,
    string ItemCatalogRevision,
    string QuestCatalogRevision);

public sealed record StressWorkerHeartbeatResponse(
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

public sealed record StressWorkerOfflineRequest(string RuntimeId);

public sealed record StressWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

// Matches GameService: the ticket id, the client's nonce, and its proof from the join request.
public sealed record StressConsumeTicketRequest(
    Guid TicketId,
    byte[]? ClientNonce,
    byte[]? Proof,
    int ProtocolVersion,
    string WorkerId,
    string RuntimeId,
    string ShardId);

public sealed record StressConsumedTicketResponse(
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

    // Matches GameService: the worker sets its lease deadline on its own clock.
    public long SessionLeaseDurationMilliseconds { get; init; }

    // Matches GameService: stress bots join without equipment.
    public IReadOnlyList<object> Equipment { get; init; } = [];

    // Matches GameService: the ticket's transport key, from which the worker derives the keys of
    // the bot's sealed datagrams.
    public byte[] TransportKey { get; init; } = [];
}

public sealed record StressSessionCredentialRequest(
    string WorkerRuntimeId,
    string SessionToken);

// Matches GameService: the heartbeat of a worker runtime's sessions, answered per session in the
// request's order.
public sealed record StressSessionHeartbeatRequest(
    string RuntimeId,
    IReadOnlyList<StressSessionHeartbeatEntry> Sessions);

public sealed record StressSessionHeartbeatEntry(Guid SimulationSessionId, string SessionToken);

public sealed record StressSessionHeartbeatResponse(IReadOnlyList<StressSessionHeartbeatResult> Sessions);

public sealed record StressSessionHeartbeatResult(
    Guid SimulationSessionId,
    StressSessionLeaseResponse? Lease,
    StressSessionHeartbeatError? Error);

public sealed record StressSessionHeartbeatError(string Code, string Message);

public sealed record StressSessionLeaseResponse(
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
    // Matches GameService: zero after a release, the lease lifetime otherwise.
    public long LeaseDurationMilliseconds { get; init; }

    // Matches GameService: stress bots never equip items.
    public IReadOnlyList<object> Equipment { get; init; } = [];
}

public sealed record StressCorpseRestoreResponse(
    DateTime DatabaseTime,
    IReadOnlyList<object> Corpses);

public sealed record StressProblemDetails(
    string Type,
    string Title,
    int Status,
    string Code,
    string Detail);

public sealed record StressIssuedTicket(
    int BotIndex,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    Guid TicketId,
    byte[] TransportKey,
    DateTime ExpiresAt);

// A bot's admission: its character and the join ticket's public id and transport key.
public sealed record StressBotAdmission(
    int BotIndex,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    Guid TicketId,
    byte[] TransportKey,
    DateTime ExpiresAt,
    string WorkerHost,
    int WorkerUdpPort,
    string ShardId,
    string WorldId,
    FullStackCharacterInventoryResponse? Inventory = null,
    Guid? LootHotspotCorpseId = null,
    StressFixtureDefinitions? FixtureDefinitions = null);

// The item definitions that GameService granted to the stress fixtures.
public sealed record StressFixtureDefinitions(string InventoryDefinitionId, string LootDefinitionId);

public sealed record StressAdmissionResult(
    StressBotAdmission? Admission,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Admission is not null;

    public static StressAdmissionResult Success(StressBotAdmission admission)
    {
        return new StressAdmissionResult(admission, null, null);
    }

    public static StressAdmissionResult Failure(string code, string message)
    {
        return new StressAdmissionResult(null, code, message);
    }
}

public sealed record StressRunTarget(
    string ShardId,
    string WorldId,
    int Capacity,
    StressWorkerRegistration? Worker);

public sealed record StressWorkerRegistration(
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
    DateTime StartedAt,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil,
    // Null in full-stack runs: GameService gates placement on them and does not expose them.
    string? ItemCatalogRevision = null,
    string? QuestCatalogRevision = null);

public sealed record StressAuthorityCounters(
    long WorkerHeartbeats,
    long TicketsIssued,
    long TicketsConsumed,
    long TicketRejections,
    long SessionHeartbeats,
    long SessionHeartbeatRequests,
    long SessionReleases,
    int PendingTickets,
    int ActiveSessions);

public sealed record StressAuthorityResult<T>(T? Value, StressProblemDetails? Problem)
{
    public bool Succeeded => Problem is null;

    public static StressAuthorityResult<T> Success(T value)
    {
        return new StressAuthorityResult<T>(value, null);
    }

    public static StressAuthorityResult<T> Failure(
        int status,
        string code,
        string detail)
    {
        var title = status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            409 => "Conflict",
            _ => "Stress Authority Error"
        };
        return new StressAuthorityResult<T>(
            default,
            new StressProblemDetails(
                $"https://mmorpg.local/problems/{code}",
                title,
                status,
                code,
                detail));
    }
}
