using AuthService.Auth;
using AuthService.Config;
using AuthService.Http;

namespace AuthService.Simulation;

public sealed class DevelopmentSimulationBotAuthority(
    DevelopmentSimulationBotOptions options,
    TimeProvider timeProvider)
{
    private const string TicketPrefix = "development-bot.";
    private static readonly TimeSpan SessionTombstoneLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InactiveIdentityLifetime = TimeSpan.FromHours(25);

    private readonly object gate = new();
    private readonly Dictionary<Guid, BotIdentity> identities = [];
    private readonly Dictionary<string, PendingBotTicket> pendingTickets =
        new(StringComparer.Ordinal);
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
            var pendingForRuntime = pendingTickets.Values.Count(ticket =>
                ticket.ExpiresAt > now
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
            if (occupiedOrReservedConnections >= placement.MaxConnections)
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_worker_capacity_reached",
                    "The selected SimulationWorker has reached its connection capacity.");
            }

            if (pendingTickets.Values.Any(ticket =>
                    ticket.BotInstanceId == request.BotInstanceId
                    && ticket.ExpiresAt > now))
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_ticket_pending",
                    "This development bot already has an unconsumed join ticket.");
            }

            if (sessions.Values.Any(session =>
                    session.BotInstanceId == request.BotInstanceId
                    && session.ReleasedAt is null
                    && session.ExpiresAt > now))
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_already_active",
                    "This development bot already has an active simulation session.");
            }

            if (!identities.TryGetValue(request.BotInstanceId, out var identity))
            {
                identity = new BotIdentity(
                    request.BotInstanceId,
                    request.BotIndex,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    $"{options.CharacterNamePrefix} {request.BotIndex}",
                    now);
                identities.Add(request.BotInstanceId, identity);
            }
            else if (identity.BotIndex != request.BotIndex)
            {
                return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                    "development_bot_identity_changed",
                    "A development bot instance cannot change its bot index while AuthService is running.");
            }
            else
            {
                identity = identity with { LastTouchedAt = now };
                identities[request.BotInstanceId] = identity;
            }

            var ticket = TicketPrefix + TokenGenerator.CreateToken();
            var expiresAt = now.Add(options.JoinTicketLifetime);
            pendingTickets.Add(
                TokenGenerator.HashToken(ticket),
                new PendingBotTicket(
                    request.BotInstanceId,
                    identity.AccountId,
                    identity.CharacterId,
                    identity.CharacterName,
                    placement.ShardId,
                    placement.WorldId,
                    placement.WorkerId,
                    placement.RuntimeId,
                    expiresAt));

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
                    ticket,
                    expiresAt));
        }
    }

    public bool TryConsumeTicket(
        ConsumeSimulationJoinTicketRequest request,
        out ServiceResult<ConsumedSimulationJoinTicketResponse> result)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ticket is null
            || !request.Ticket.StartsWith(TicketPrefix, StringComparison.Ordinal))
        {
            result = default!;
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            var ticketHash = TokenGenerator.HashToken(request.Ticket);
            if (!pendingTickets.TryGetValue(ticketHash, out var ticket)
                || ticket.ExpiresAt <= now)
            {
                pendingTickets.Remove(ticketHash);
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

            pendingTickets.Remove(ticketHash);
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
                    false)
                {
                    IsSyntheticBot = true
                });
            return true;
        }
    }

    public bool TryHeartbeatSession(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionCredentialRequest request,
        out ServiceResult<SimulationSessionLeaseResponse> result)
    {
        return TryUpdateSession(
            simulationSessionId,
            workerId,
            request,
            release: false,
            out result);
    }

    public bool TryReleaseSession(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionCredentialRequest request,
        out ServiceResult<SimulationSessionLeaseResponse> result)
    {
        return TryUpdateSession(
            simulationSessionId,
            workerId,
            request,
            release: true,
            out result);
    }

    public DevelopmentSimulationBotAuthoritySnapshot CaptureSnapshot()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            Cleanup(now);
            return new DevelopmentSimulationBotAuthoritySnapshot(
                identities.Count,
                pendingTickets.Values.Count(ticket => ticket.ExpiresAt > now),
                sessions.Values.Count(session =>
                    session.ReleasedAt is null && session.ExpiresAt > now),
                sessions.Values.Count(session =>
                    session.ReleasedAt is not null || session.ExpiresAt <= now));
        }
    }

    private bool TryUpdateSession(
        Guid simulationSessionId,
        string workerId,
        SimulationSessionCredentialRequest request,
        bool release,
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
                result = ServiceResult<SimulationSessionLeaseResponse>.Unauthorized(
                    "invalid_simulation_session",
                    "The development bot simulation session is invalid, released, expired, or owned by another worker runtime.");
                return true;
            }

            var updated = release
                ? session with { ReleasedAt = now }
                : session with { ExpiresAt = now.Add(options.SessionLeaseLifetime) };
            sessions[simulationSessionId] = updated;
            if (identities.TryGetValue(updated.BotInstanceId, out var identity))
            {
                identities[updated.BotInstanceId] = identity with { LastTouchedAt = now };
            }
            result = ServiceResult<SimulationSessionLeaseResponse>.Ok(
                new SimulationSessionLeaseResponse(
                    updated.Id,
                    updated.CharacterId,
                    updated.ShardId,
                    updated.WorkerId,
                    updated.WorkerRuntimeId,
                    release ? now : updated.ExpiresAt,
                    release));
            return true;
        }
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

        if (!IsValidIdentifier(request.ShardId))
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.BadRequest(
                "invalid_shard_id",
                "Shard id is invalid.");
        }

        return null;
    }

    private void Cleanup(DateTime now)
    {
        foreach (var ticketHash in pendingTickets
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            pendingTickets.Remove(ticketHash);
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
            .Where(ticket => ticket.ExpiresAt > now)
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

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
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
        DateTime ExpiresAt);

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
