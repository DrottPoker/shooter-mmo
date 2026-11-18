using Mmorpg.GameProtocol;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Placement.Development;

// Bot tickets work as a player's: a public id and a transport key, and the worker's consume
// proves the key. A consumed ticket stays known until its expiry, so a second consume of it is
// refused here instead of reaching the database.
internal sealed class DevelopmentSimulationBotAuthority(
    DevelopmentSimulationBotOptions options,
    TimeProvider timeProvider)
{
    // Synthetic bots own no items, so any positive capacity keeps them at full speed.
    private const long SyntheticBotCarryCapacity = 1;
    private static readonly TimeSpan SessionTombstoneLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InactiveIdentityLifetime = TimeSpan.FromHours(25);

    private readonly object gate = new();
    private readonly Dictionary<Guid, BotIdentity> identities = [];
    private readonly Dictionary<Guid, PendingBotTicket> pendingTickets = [];
    private readonly Dictionary<Guid, BotSession> sessions = [];

    public ServiceResult<DevelopmentSimulationBotTicketResponse> IssueTicket(
        DevelopmentSimulationBotTicketRequest request,
        DevelopmentSimulationBotPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(placement);

        var validation = ValidateTicketRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            if (sessions.Values.Any(session =>
                    session.BotInstanceId == request.BotInstanceId
                    && session.ReleasedAt is null
                    && session.ExpiresAt > now))
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_already_active",
                    "This development bot already has an active simulation session.");
            }

            if (identities.TryGetValue(request.BotInstanceId, out var known)
                && known.BotIndex != request.BotIndex)
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_identity_changed",
                    "A development bot instance cannot change its bot index while GameService is running.");
            }

            // A bot asks again only after it gave up its connection, so its own unconsumed
            // ticket is superseded, as a player's is, and stops holding a connection.
            foreach (var supersededId in pendingTickets
                         .Where(pair => pair.Value.BotInstanceId == request.BotInstanceId && !pair.Value.Consumed)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                RetireTicket(supersededId);
            }

            var pendingForRuntime = pendingTickets.Values.Count(ticket =>
                ticket.ExpiresAt > now
                && !ticket.Consumed
                && string.Equals(ticket.WorkerId, placement.WorkerId, StringComparison.Ordinal)
                && string.Equals(ticket.WorkerRuntimeId, placement.RuntimeId, StringComparison.Ordinal));
            var activeBotsForRuntime = sessions.Values.Count(session =>
                session.ReleasedAt is null
                && session.ExpiresAt > now
                && string.Equals(session.WorkerId, placement.WorkerId, StringComparison.Ordinal)
                && string.Equals(
                    session.WorkerRuntimeId,
                    placement.RuntimeId,
                    StringComparison.Ordinal));
            var occupiedOrReservedConnections = Math.Max(
                    placement.ReportedActiveConnections,
                    placement.ActiveDatabaseSessions + activeBotsForRuntime)
                + placement.PendingDatabaseTickets
                + pendingForRuntime;
            // Bots never take the last connections, so real local players can still join.
            if (occupiedOrReservedConnections
                >= placement.MaxConnections - options.ReservedPlayerConnections)
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_worker_capacity_reached",
                    "The selected SimulationWorker has reached its connection capacity.");
            }

            var identity = known is null
                ? new BotIdentity(
                    request.BotInstanceId,
                    request.BotIndex,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    $"{options.CharacterNamePrefix} {request.BotIndex}",
                    now)
                : known with { LastTouchedAt = now };
            identities[request.BotInstanceId] = identity;

            var ticketId = Guid.NewGuid();
            var transportKey = RealtimeTransportKeys.CreateTransportKey();
            var expiresAt = now.Add(options.JoinTicketLifetime);
            pendingTickets.Add(
                ticketId,
                new PendingBotTicket(
                    request.BotInstanceId,
                    identity.AccountId,
                    identity.CharacterId,
                    identity.CharacterName,
                    placement.ShardId,
                    placement.WorldId,
                    placement.WorkerId,
                    placement.RuntimeId,
                    expiresAt,
                    (byte[])transportKey.Clone()));

            return ServiceResult<DevelopmentSimulationBotTicketResponse>.Ok(
                new DevelopmentSimulationBotTicketResponse(
                    identity.AccountId,
                    identity.CharacterId,
                    identity.CharacterName,
                    placement.ShardId,
                    placement.WorldId,
                    new SimulationEndpointResponse(
                        placement.WorkerId,
                        placement.RuntimeId,
                        placement.Host,
                        placement.UdpPort,
                        placement.ProtocolVersion,
                        placement.SimulationRevision,
                        placement.CollisionRevision),
                    ticketId,
                    transportKey,
                    expiresAt));
        }
    }

    public bool TryConsumeTicket(
        ConsumeSimulationJoinTicketRequest request,
        out ServiceResult<ConsumedSimulationJoinTicketResponse> result)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            if (!pendingTickets.TryGetValue(request.TicketId, out var ticket))
            {
                result = default!;
                return false;
            }

            // A wrong proof leaves the ticket as it was, as GameService does with a player's.
            if (ticket.Consumed || ticket.ExpiresAt <= now || !ProvesTransportKey(request, ticket))
            {
                result = ServiceResult<ConsumedSimulationJoinTicketResponse>.Unauthorized(
                    "invalid_join_ticket",
                    "The development bot join ticket is invalid, expired, or already consumed.");
                return true;
            }

            if (!string.Equals(request.WorkerId, ticket.WorkerId, StringComparison.Ordinal)
                || !string.Equals(request.RuntimeId, ticket.WorkerRuntimeId, StringComparison.Ordinal)
                || !string.Equals(request.ShardId, ticket.ShardId, StringComparison.Ordinal))
            {
                result = ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                    "wrong_simulation_assignment",
                    "The development bot join ticket does not match the requesting worker runtime and shard.");
                return true;
            }

            if (sessions.Values.Any(session =>
                    session.BotInstanceId == ticket.BotInstanceId
                    && session.ReleasedAt is null
                    && session.ExpiresAt > now))
            {
                result = ServiceResult<ConsumedSimulationJoinTicketResponse>.Conflict(
                    "development_bot_already_active",
                    "The development bot already has an active simulation session.");
                return true;
            }

            var transportKey = (byte[])ticket.TransportKey.Clone();
            Array.Clear(ticket.TransportKey);
            pendingTickets[request.TicketId] = ticket with { Consumed = true };
            var sessionToken = TokenGenerator.CreateToken();
            var session = new BotSession(
                Guid.NewGuid(),
                ticket.BotInstanceId,
                TokenGenerator.HashToken(sessionToken),
                ticket.CharacterId,
                ticket.ShardId,
                ticket.WorkerId,
                ticket.WorkerRuntimeId,
                now.Add(options.SessionLeaseLifetime),
                null);
            sessions.Add(session.Id, session);
            if (identities.TryGetValue(ticket.BotInstanceId, out var identity))
            {
                identities[ticket.BotInstanceId] = identity with { LastTouchedAt = now };
            }

            result = ServiceResult<ConsumedSimulationJoinTicketResponse>.Ok(
                new ConsumedSimulationJoinTicketResponse(
                    ticket.AccountId,
                    ticket.CharacterId,
                    ticket.CharacterName,
                    ticket.ShardId,
                    ticket.WorldId,
                    ticket.WorkerId,
                    ticket.WorkerRuntimeId,
                    session.Id,
                    sessionToken,
                    session.ExpiresAt,
                    0,
                    0,
                    SyntheticBotCarryCapacity,
                    false)
                {
                    IsSyntheticBot = true,
                    SessionLeaseDurationMilliseconds =
                        (long)options.SessionLeaseLifetime.TotalMilliseconds,
                    TransportKey = transportKey
                });
            return true;
        }
    }

    private static bool ProvesTransportKey(ConsumeSimulationJoinTicketRequest request, PendingBotTicket ticket)
    {
        var proofKey = RealtimeTransportKeys.DeriveProofKey(ticket.TransportKey);
        try
        {
            return request.ProtocolVersion is >= 1 and <= ushort.MaxValue
                && RealtimeJoinProof.Verify(
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

    // A superseded ticket stays known as consumed until its expiry, with its key zeroed.
    private void RetireTicket(Guid ticketId)
    {
        var ticket = pendingTickets[ticketId];
        Array.Clear(ticket.TransportKey);
        pendingTickets[ticketId] = ticket with { Consumed = true };
    }

    private void RemoveTicket(Guid ticketId)
    {
        if (pendingTickets.Remove(ticketId, out var ticket))
        {
            Array.Clear(ticket.TransportKey);
        }
    }

    // Renews the bot sessions among the sessions of a worker runtime's heartbeat under one lock.
    // The answer names every entry whose id is a bot session; any other entry is not a bot's, so
    // the database answers it.
    public IReadOnlyDictionary<Guid, SimulationSessionHeartbeatResult> HeartbeatSessions(
        string workerId,
        string workerRuntimeId,
        IReadOnlyList<SimulationSessionHeartbeatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var results = new Dictionary<Guid, SimulationSessionHeartbeatResult>();
        lock (gate)
        {
            if (sessions.Count == 0)
            {
                return results;
            }

            Cleanup(now);
            foreach (var entry in entries)
            {
                if (!sessions.TryGetValue(entry.SimulationSessionId, out var session))
                {
                    continue;
                }

                var result = UpdateSessionLocked(
                    session,
                    workerId,
                    new SimulationSessionCredentialRequest(workerRuntimeId, entry.SessionToken),
                    release: false,
                    now);
                results[entry.SimulationSessionId] = result.Succeeded
                    ? SimulationSessionHeartbeatResult.Renewed(result.Value!)
                    : SimulationSessionHeartbeatResult.Refused(entry.SimulationSessionId, result.Error!);
            }
        }

        return results;
    }

    public bool TryReleaseSession(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionCredentialRequest request,
        out ServiceResult<SimulationSessionLeaseResponse> result)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            if (!sessions.TryGetValue(simulationSessionId, out var session))
            {
                result = default!;
                return false;
            }

            result = UpdateSessionLocked(session, workerId, request, release: true, now);
            return true;
        }
    }

    // Bot tickets and sessions live only in this process, so player placement reads them
    // here; otherwise both paths could reserve the same worker connections.
    public IReadOnlyList<DevelopmentSimulationBotReservation> CaptureReservations()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            var pending = pendingTickets.Values
                .Where(ticket => ticket.ExpiresAt > now && !ticket.Consumed)
                .Select(ticket => (ticket.WorkerId, ticket.WorkerRuntimeId, Session: 0, Ticket: 1));
            var active = sessions.Values
                .Where(session => session.ReleasedAt is null && session.ExpiresAt > now)
                .Select(session => (session.WorkerId, session.WorkerRuntimeId, Session: 1, Ticket: 0));
            return pending
                .Concat(active)
                .GroupBy(entry => (entry.WorkerId, entry.WorkerRuntimeId))
                .Select(group => new DevelopmentSimulationBotReservation(
                    group.Key.WorkerId,
                    group.Key.WorkerRuntimeId,
                    group.Sum(entry => entry.Session),
                    group.Sum(entry => entry.Ticket)))
                .ToArray();
        }
    }

    public DevelopmentSimulationBotAuthoritySnapshot CaptureSnapshot()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            return new DevelopmentSimulationBotAuthoritySnapshot(
                identities.Count,
                pendingTickets.Values.Count(ticket => ticket.ExpiresAt > now && !ticket.Consumed),
                sessions.Values.Count(session =>
                    session.ReleasedAt is null && session.ExpiresAt > now),
                sessions.Values.Count(session =>
                    session.ReleasedAt is not null || session.ExpiresAt <= now));
        }
    }

    // Renews or releases a bot session that the caller found under the lock.
    private ServiceResult<SimulationSessionLeaseResponse> UpdateSessionLocked(
        BotSession session,
        string workerId,
        SimulationSessionCredentialRequest request,
        bool release,
        DateTime now)
    {
        if (session.ReleasedAt is not null
            || session.ExpiresAt <= now
            || !string.Equals(workerId, session.WorkerId, StringComparison.Ordinal)
            || !string.Equals(request.WorkerRuntimeId, session.WorkerRuntimeId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.SessionToken)
            || !string.Equals(
                TokenGenerator.HashToken(request.SessionToken.Trim()),
                session.TokenHash,
                StringComparison.Ordinal))
        {
            return ServiceResult<SimulationSessionLeaseResponse>.Unauthorized(
                "invalid_simulation_session",
                "The development bot simulation session is invalid, released, expired, or owned by another worker runtime.");
        }

        var updated = release
            ? session with { ReleasedAt = now }
            : session with { ExpiresAt = now.Add(options.SessionLeaseLifetime) };
        sessions[session.Id] = updated;
        if (identities.TryGetValue(updated.BotInstanceId, out var identity))
        {
            identities[updated.BotInstanceId] = identity with { LastTouchedAt = now };
        }

        return ServiceResult<SimulationSessionLeaseResponse>.Ok(
            new SimulationSessionLeaseResponse(
                updated.Id,
                updated.CharacterId,
                updated.ShardId,
                updated.WorkerId,
                updated.WorkerRuntimeId,
                release ? now : updated.ExpiresAt,
                0,
                0,
                SyntheticBotCarryCapacity,
                release)
            {
                LeaseDurationMilliseconds = release
                    ? 0
                    : (long)options.SessionLeaseLifetime.TotalMilliseconds
            });
    }

    private ServiceResult<DevelopmentSimulationBotTicketResponse>? ValidateTicketRequest(
        DevelopmentSimulationBotTicketRequest request)
    {
        if (!options.Enabled)
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.NotFound(
                "development_simulation_bots_disabled",
                "Development simulation bots are disabled.");
        }

        if (request.BotInstanceId == Guid.Empty)
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.BadRequest(
                "invalid_development_bot_instance_id",
                "Development bot instance id is required.");
        }

        if (request.BotIndex is < 1 or > 1_000_000)
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.BadRequest(
                "invalid_development_bot_index",
                "Development bot index must be between 1 and 1000000.");
        }

        if (!Identifiers.IsServiceIdentifier(request.ShardId))
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.BadRequest(
                "invalid_shard_id",
                "Shard id is invalid.");
        }

        return null;
    }

    private void Cleanup(DateTime now)
    {
        foreach (var ticketId in pendingTickets
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RemoveTicket(ticketId);
        }

        foreach (var sessionId in sessions
                     .Where(pair =>
                         pair.Value.ReleasedAt?.Add(SessionTombstoneLifetime) <= now
                         || (pair.Value.ReleasedAt is null
                             && pair.Value.ExpiresAt.Add(SessionTombstoneLifetime) <= now))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            sessions.Remove(sessionId);
        }

        var liveIdentityIds = pendingTickets.Values
            .Where(ticket => ticket.ExpiresAt > now && !ticket.Consumed)
            .Select(ticket => ticket.BotInstanceId)
            .Concat(sessions.Values
                .Where(session => session.ReleasedAt is null && session.ExpiresAt > now)
                .Select(session => session.BotInstanceId))
            .ToHashSet();
        foreach (var botInstanceId in identities
                     .Where(pair =>
                         !liveIdentityIds.Contains(pair.Key)
                         && pair.Value.LastTouchedAt.Add(InactiveIdentityLifetime) <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            identities.Remove(botInstanceId);
        }
    }

    private sealed record BotIdentity(
        Guid BotInstanceId,
        int BotIndex,
        Guid AccountId,
        Guid CharacterId,
        string CharacterName,
        DateTime LastTouchedAt);

    private sealed record PendingBotTicket(
        Guid BotInstanceId,
        Guid AccountId,
        Guid CharacterId,
        string CharacterName,
        string ShardId,
        string WorldId,
        string WorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt,
        byte[] TransportKey)
    {
        public bool Consumed { get; init; }
    }

    private sealed record BotSession(
        Guid Id,
        Guid BotInstanceId,
        string TokenHash,
        Guid CharacterId,
        string ShardId,
        string WorkerId,
        string WorkerRuntimeId,
        DateTime ExpiresAt,
        DateTime? ReleasedAt);
}
