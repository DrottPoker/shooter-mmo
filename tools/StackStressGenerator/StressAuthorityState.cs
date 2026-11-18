using System.Security.Cryptography;
using System.Text;
using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.StackStressGenerator;

public sealed class StressAuthorityState
{
    // The rule set of the development Shard, which the worker's combat rules define.
    public const string ShardRuleSet = "mvp-open-risk";

    private const long SyntheticCarryCapacity = 200;
    private static readonly TimeSpan WorkerLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);
    // As long as GameService grants by default, which outlasts the worker's outage tolerance plus its
    // session heartbeat interval, the shortest lease the worker accepts.
    private static readonly TimeSpan SessionLeaseDuration = TimeSpan.FromSeconds(150);

    private readonly object gate = new();
    private readonly StressGeneratorOptions options;
    private readonly Dictionary<Guid, PendingTicket> pendingTickets = [];
    private readonly Dictionary<Guid, StressSession> sessions = [];
    private readonly TaskCompletionSource<StressWorkerRegistration> workerRegistered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StressWorkerRegistration? worker;
    private long workerHeartbeats;
    private long ticketsIssued;
    private long ticketsConsumed;
    private long ticketRejections;
    private long sessionHeartbeats;
    private long sessionHeartbeatRequests;
    private long sessionReleases;

    public StressAuthorityState(StressGeneratorOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsAuthorized(string? workerId, string? secret)
    {
        if (!string.Equals(workerId, options.WorkerId, StringComparison.Ordinal)
            || secret is null)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(options.WorkerSecret!);
        var actual = Encoding.UTF8.GetBytes(secret);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public StressAuthorityResult<StressWorkerHeartbeatResponse> RegisterWorker(
        string workerId,
        StressWorkerHeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(workerId, options.WorkerId, StringComparison.Ordinal)
            || !string.Equals(request.FleetId, options.FleetId, StringComparison.Ordinal)
            || !string.Equals(request.NodeId, options.NodeId, StringComparison.Ordinal)
            || !string.Equals(request.ShardId, options.ShardId, StringComparison.Ordinal))
        {
            return StressAuthorityResult<StressWorkerHeartbeatResponse>.Failure(
                409,
                "simulation_topology_mismatch",
                "The worker heartbeat does not match the configured stress topology.");
        }

        if (string.IsNullOrWhiteSpace(request.RuntimeId)
            || string.IsNullOrWhiteSpace(request.Host)
            || request.UdpPort <= 0
            || request.MaxConnections <= 0
            || request.ActiveConnections < 0
            || request.ActiveConnections > request.MaxConnections
            || request.ProtocolVersion != RealtimeProtocol.Version
            || string.IsNullOrWhiteSpace(request.SimulationRevision)
            || string.IsNullOrWhiteSpace(request.CollisionRevision)
            || string.IsNullOrWhiteSpace(request.ItemCatalogRevision)
            || string.IsNullOrWhiteSpace(request.QuestCatalogRevision))
        {
            return StressAuthorityResult<StressWorkerHeartbeatResponse>.Failure(
                400,
                "invalid_worker_heartbeat",
                "The worker heartbeat contains invalid runtime, endpoint, capacity, protocol, or revision metadata.");
        }

        var now = DateTime.UtcNow;
        var registration = new StressWorkerRegistration(
            workerId,
            request.RuntimeId,
            request.FleetId,
            request.NodeId,
            request.ShardId,
            options.WorldId,
            request.Host,
            request.UdpPort,
            request.MaxConnections,
            request.ActiveConnections,
            request.ProtocolVersion,
            request.SimulationRevision,
            request.CollisionRevision,
            request.StartedAt,
            now,
            now.Add(WorkerLeaseDuration),
            request.ItemCatalogRevision,
            request.QuestCatalogRevision);

        lock (gate)
        {
            if (worker is not null
                && !string.Equals(worker.RuntimeId, registration.RuntimeId, StringComparison.Ordinal))
            {
                pendingTickets.Clear();
                sessions.Clear();
            }

            worker = registration;
            workerHeartbeats++;
        }

        workerRegistered.TrySetResult(registration);
        return StressAuthorityResult<StressWorkerHeartbeatResponse>.Success(
            ToHeartbeatResponse(registration));
    }

    public StressAuthorityResult<StressWorkerOfflineResponse> MarkWorkerOffline(
        string workerId,
        StressWorkerOfflineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (worker is null
                || !string.Equals(workerId, worker.WorkerId, StringComparison.Ordinal)
                || !string.Equals(request.RuntimeId, worker.RuntimeId, StringComparison.Ordinal))
            {
                return StressAuthorityResult<StressWorkerOfflineResponse>.Failure(
                    409,
                    "worker_runtime_changed",
                    "The stress authority no longer owns the requested worker runtime.");
            }

            worker = worker with { OnlineUntil = DateTime.UtcNow };
            pendingTickets.Clear();
            sessions.Clear();
            return StressAuthorityResult<StressWorkerOfflineResponse>.Success(
                new StressWorkerOfflineResponse(workerId, request.RuntimeId, DateTime.UtcNow));
        }
    }

    public async Task<StressWorkerRegistration> WaitForWorkerAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await workerRegistered.Task.WaitAsync(timeout, cancellationToken);
    }

    public StressIssuedTicket IssueTicket(int botIndex)
    {
        lock (gate)
        {
            if (worker is null || worker.OnlineUntil <= DateTime.UtcNow)
            {
                throw new InvalidOperationException(
                    "A fresh SimulationWorker registration is required before stress tickets can be issued.");
            }

            var accountId = CreateDeterministicGuid("account", botIndex);
            var characterId = CreateDeterministicGuid("character", botIndex);
            var characterName = $"Stress Bot {botIndex}";
            var ticketId = Guid.NewGuid();
            var transportKey = RealtimeTransportKeys.CreateTransportKey();
            var expiresAt = DateTime.UtcNow.Add(TicketLifetime);
            pendingTickets.Add(
                ticketId,
                new PendingTicket(
                    accountId,
                    characterId,
                    characterName,
                    worker.WorkerId,
                    worker.RuntimeId,
                    worker.ShardId,
                    worker.WorldId,
                    expiresAt,
                    (byte[])transportKey.Clone()));
            ticketsIssued++;
            return new StressIssuedTicket(
                botIndex,
                accountId,
                characterId,
                characterName,
                ticketId,
                transportKey,
                expiresAt);
        }
    }

    public StressAuthorityResult<StressConsumedTicketResponse> ConsumeTicket(
        StressConsumeTicketRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            // As GameService: the proof comes first, and a wrong one leaves the ticket as it was.
            if (!pendingTickets.TryGetValue(request.TicketId, out var ticket)
                || ticket.ExpiresAt <= DateTime.UtcNow
                || !ProvesTransportKey(request, ticket))
            {
                if (ticket is not null && ticket.ExpiresAt <= DateTime.UtcNow)
                {
                    RemoveTicket(request.TicketId);
                }

                ticketRejections++;
                return StressAuthorityResult<StressConsumedTicketResponse>.Failure(
                    401,
                    "invalid_join_ticket",
                    "The stress join ticket is invalid, expired, or already consumed.");
            }

            if (!string.Equals(request.WorkerId, ticket.WorkerId, StringComparison.Ordinal)
                || !string.Equals(request.RuntimeId, ticket.WorkerRuntimeId, StringComparison.Ordinal)
                || !string.Equals(request.ShardId, ticket.ShardId, StringComparison.Ordinal))
            {
                ticketRejections++;
                return StressAuthorityResult<StressConsumedTicketResponse>.Failure(
                    409,
                    "wrong_simulation_assignment",
                    "The stress join ticket does not match the requesting worker runtime and shard.");
            }

            var transportKey = (byte[])ticket.TransportKey.Clone();
            RemoveTicket(request.TicketId);
            var sessionId = Guid.NewGuid();
            var sessionToken = CreateToken();
            var expiresAt = DateTime.UtcNow.Add(SessionLeaseDuration);
            sessions.Add(
                sessionId,
                new StressSession(
                    sessionId,
                    HashToken(sessionToken),
                    ticket.AccountId,
                    ticket.CharacterId,
                    ticket.CharacterName,
                    ticket.ShardId,
                    ticket.WorldId,
                    ticket.WorkerId,
                    ticket.WorkerRuntimeId,
                    expiresAt));
            ticketsConsumed++;
            return StressAuthorityResult<StressConsumedTicketResponse>.Success(
                new StressConsumedTicketResponse(
                    ticket.AccountId,
                    ticket.CharacterId,
                    ticket.CharacterName,
                    ticket.ShardId,
                    ticket.WorldId,
                    ticket.WorkerId,
                    ticket.WorkerRuntimeId,
                    sessionId,
                    sessionToken,
                    expiresAt,
                    0,
                    0,
                    SyntheticCarryCapacity,
                    false)
                {
                    IsSyntheticBot = true,
                    SessionLeaseDurationMilliseconds = (long)SessionLeaseDuration.TotalMilliseconds,
                    TransportKey = transportKey
                });
        }
    }

    private static bool ProvesTransportKey(StressConsumeTicketRequest request, PendingTicket ticket)
    {
        if (request.ProtocolVersion is < 1 or > ushort.MaxValue)
        {
            return false;
        }

        var proofKey = RealtimeTransportKeys.DeriveProofKey(ticket.TransportKey);
        try
        {
            return RealtimeJoinProof.Verify(
                proofKey,
                (ushort)request.ProtocolVersion,
                request.TicketId,
                ticket.ShardId,
                request.ClientNonce,
                request.Proof);
        }
        finally
        {
            Array.Clear(proofKey);
        }
    }

    private void RemoveTicket(Guid ticketId)
    {
        if (pendingTickets.Remove(ticketId, out var ticket))
        {
            Array.Clear(ticket.TransportKey);
        }
    }

    // Renews the leases of the worker runtime's sessions; a session that is gone, expired, or
    // proved by other credentials is refused on its own, as GameService refuses it.
    public StressAuthorityResult<StressSessionHeartbeatResponse> HeartbeatSessions(
        string workerId,
        StressSessionHeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(workerId, options.WorkerId, StringComparison.Ordinal)
            || request.Sessions is null
            || request.Sessions.Count == 0)
        {
            return StressAuthorityResult<StressSessionHeartbeatResponse>.Failure(
                400,
                "invalid_simulation_session_heartbeat",
                "The stress session heartbeat names no session of the stress worker.");
        }

        lock (gate)
        {
            sessionHeartbeatRequests++;
            var results = new StressSessionHeartbeatResult[request.Sessions.Count];
            for (var index = 0; index < results.Length; index++)
            {
                var entry = request.Sessions[index];
                var validation = ValidateSession(
                    entry.SimulationSessionId,
                    new StressSessionCredentialRequest(request.RuntimeId, entry.SessionToken));
                if (!validation.Succeeded)
                {
                    results[index] = new StressSessionHeartbeatResult(
                        entry.SimulationSessionId,
                        null,
                        new StressSessionHeartbeatError(validation.Problem!.Code, validation.Problem.Detail));
                    continue;
                }

                var refreshed = validation.Value! with
                {
                    ExpiresAt = DateTime.UtcNow.Add(SessionLeaseDuration)
                };
                sessions[entry.SimulationSessionId] = refreshed;
                sessionHeartbeats++;
                results[index] = new StressSessionHeartbeatResult(
                    entry.SimulationSessionId,
                    ToLeaseResponse(refreshed, false),
                    null);
            }

            return StressAuthorityResult<StressSessionHeartbeatResponse>.Success(
                new StressSessionHeartbeatResponse(results));
        }
    }

    public StressAuthorityResult<StressSessionLeaseResponse> ReleaseSession(
        Guid sessionId,
        StressSessionCredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            var validation = ValidateSession(sessionId, request);
            if (!validation.Succeeded)
            {
                return StressAuthorityResult<StressSessionLeaseResponse>.Failure(
                    validation.Problem!.Status,
                    validation.Problem.Code,
                    validation.Problem.Detail);
            }

            sessions.Remove(sessionId);
            sessionReleases++;
            return StressAuthorityResult<StressSessionLeaseResponse>.Success(
                ToLeaseResponse(validation.Value!, true));
        }
    }

    public StressAuthorityCounters CaptureCounters()
    {
        lock (gate)
        {
            return new StressAuthorityCounters(
                workerHeartbeats,
                ticketsIssued,
                ticketsConsumed,
                ticketRejections,
                sessionHeartbeats,
                sessionHeartbeatRequests,
                sessionReleases,
                pendingTickets.Count,
                sessions.Count);
        }
    }

    public StressWorkerRegistration? CaptureWorkerRegistration()
    {
        lock (gate)
        {
            return worker;
        }
    }

    private StressAuthorityResult<StressSession> ValidateSession(
        Guid sessionId,
        StressSessionCredentialRequest request)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            return StressAuthorityResult<StressSession>.Failure(
                404,
                "simulation_session_not_found",
                "The stress simulation session does not exist.");
        }

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            sessions.Remove(sessionId);
            return StressAuthorityResult<StressSession>.Failure(
                401,
                "simulation_session_expired",
                "The stress simulation session lease expired.");
        }

        if (!string.Equals(request.WorkerRuntimeId, session.WorkerRuntimeId, StringComparison.Ordinal)
            || !string.Equals(HashToken(request.SessionToken ?? string.Empty), session.TokenHash, StringComparison.Ordinal))
        {
            return StressAuthorityResult<StressSession>.Failure(
                409,
                "simulation_session_changed",
                "The stress simulation session credentials do not match the active generation.");
        }

        return StressAuthorityResult<StressSession>.Success(session);
    }

    private Guid CreateDeterministicGuid(string kind, int botIndex)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{kind}:{options.Seed}:{botIndex}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static StressWorkerHeartbeatResponse ToHeartbeatResponse(
        StressWorkerRegistration registration)
    {
        return new StressWorkerHeartbeatResponse(
            registration.WorkerId,
            registration.RuntimeId,
            registration.FleetId,
            registration.NodeId,
            registration.ShardId,
            registration.WorldId,
            ShardRuleSet,
            registration.Host,
            registration.UdpPort,
            registration.MaxConnections,
            registration.ActiveConnections,
            registration.ProtocolVersion,
            registration.SimulationRevision,
            registration.CollisionRevision,
            registration.ItemCatalogRevision!,
            registration.QuestCatalogRevision!,
            registration.LastHeartbeatAt,
            registration.OnlineUntil);
    }

    private static StressSessionLeaseResponse ToLeaseResponse(
        StressSession session,
        bool released)
    {
        return new StressSessionLeaseResponse(
            session.SessionId,
            session.CharacterId,
            session.ShardId,
            session.WorkerId,
            session.WorkerRuntimeId,
            released ? DateTime.UtcNow : session.ExpiresAt,
            0,
            0,
            SyntheticCarryCapacity,
            released)
        {
            LeaseDurationMilliseconds = released
                ? 0
                : (long)SessionLeaseDuration.TotalMilliseconds
        };
    }

    private static string CreateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private sealed record PendingTicket(
        Guid AccountId,
        Guid CharacterId,
        string CharacterName,
        string WorkerId,
        string WorkerRuntimeId,
        string ShardId,
        string WorldId,
        DateTime ExpiresAt,
        byte[] TransportKey);

    private sealed record StressSession(
        Guid SessionId,
        string TokenHash,
        Guid AccountId,
        Guid CharacterId,
        string CharacterName,
        string ShardId,
        string WorldId,
        string WorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt);
}
