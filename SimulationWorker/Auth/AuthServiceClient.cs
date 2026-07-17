using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShooterMmo.GameSimulation;
using ShooterMmo.Shared.Health;

namespace SimulationWorker.Auth;

public sealed class AuthServiceClient(HttpClient httpClient)
{
    private const int MaximumRestoredCorpses = 4_096;

    public async Task<DependencyHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/health/ready", cancellationToken);
            return new DependencyHealth(
                "auth-service",
                new Uri(httpClient.BaseAddress!, "/health/ready").ToString(),
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new DependencyHealth(
                "auth-service",
                httpClient.BaseAddress!.ToString(),
                false,
                "Request timed out.");
        }
        catch (HttpRequestException)
        {
            return new DependencyHealth(
                "auth-service",
                httpClient.BaseAddress!.ToString(),
                false,
                "Connection failed.");
        }
    }

    public Task<AuthServiceResult<SimulationWorkerHeartbeatResponse>>
        HeartbeatSimulationWorkerAsync(
            string workerId,
            SimulationWorkerHeartbeatRequest request,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationWorkerHeartbeatRequest, SimulationWorkerHeartbeatResponse>(
            $"/api/simulation-workers/{Uri.EscapeDataString(workerId)}/heartbeat",
            request,
            response => string.Equals(response.WorkerId, workerId, StringComparison.Ordinal)
                && string.Equals(response.RuntimeId, request.RuntimeId, StringComparison.Ordinal)
                && string.Equals(response.FleetId, request.FleetId, StringComparison.Ordinal)
                && string.Equals(response.NodeId, request.NodeId, StringComparison.Ordinal)
                && string.Equals(response.ShardId, request.ShardId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(response.WorldId)
                && string.Equals(response.Host, request.Host, StringComparison.OrdinalIgnoreCase)
                && response.UdpPort == request.UdpPort
                && response.MaxConnections == request.MaxConnections
                && response.ActiveConnections == request.ActiveConnections
                && response.ProtocolVersion == request.ProtocolVersion
                && string.Equals(
                    response.SimulationRevision,
                    request.SimulationRevision,
                    StringComparison.Ordinal)
                && string.Equals(
                    response.CollisionRevision,
                    request.CollisionRevision,
                    StringComparison.Ordinal)
                && response.LastHeartbeatAt != default
                && response.OnlineUntil > response.LastHeartbeatAt,
            cancellationToken);
    }

    public Task<AuthServiceResult<SimulationWorkerOfflineResponse>>
        MarkSimulationWorkerOfflineAsync(
            string workerId,
            string runtimeId,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationWorkerOfflineRequest, SimulationWorkerOfflineResponse>(
            $"/api/simulation-workers/{Uri.EscapeDataString(workerId)}/offline",
            new SimulationWorkerOfflineRequest(runtimeId),
            response => string.Equals(response.WorkerId, workerId, StringComparison.Ordinal)
                && string.Equals(response.RuntimeId, runtimeId, StringComparison.Ordinal)
                && response.OfflineAt != default,
            cancellationToken);
    }

    public Task<AuthServiceResult<CorpseRestoreResponse>> RestoreDurableCorpsesAsync(
        string workerId,
        string workerRuntimeId,
        string shardId,
        CancellationToken cancellationToken)
    {
        var path = $"/api/simulation-workers/{Uri.EscapeDataString(workerId)}/corpses"
            + $"?workerRuntimeId={Uri.EscapeDataString(workerRuntimeId)}"
            + $"&shardId={Uri.EscapeDataString(shardId)}";
        return GetAsync<CorpseRestoreResponse>(
            path,
            response => IsValidCorpseRestore(response, shardId),
            cancellationToken);
    }

    public Task<AuthServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeJoinTicketAsync(
        string ticket,
        string workerId,
        string runtimeId,
        string shardId,
        CancellationToken cancellationToken)
    {
        return PostAsync<ConsumeSimulationJoinTicketRequest, ConsumedSimulationJoinTicketResponse>(
            "/api/simulation-join-tickets/consume",
            new ConsumeSimulationJoinTicketRequest(ticket, workerId, runtimeId, shardId),
            response => IsValidConsumedTicket(response, workerId, runtimeId, shardId),
            cancellationToken);
    }

    public Task<AuthServiceResult<PlayerDeathPartitionResponse>> ProcessPlayerDeathAsync(
        Guid simulationSessionId,
        SimulationPlayerDeathRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationPlayerDeathRequest, PlayerDeathPartitionResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/player-deaths",
            request,
            response => IsValidPlayerDeathPartition(response, request),
            cancellationToken);
    }

    public Task<AuthServiceResult<SimulationSessionLeaseResponse>>
        HeartbeatSimulationSessionAsync(
            Guid simulationSessionId,
            string workerRuntimeId,
            string sessionToken,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationSessionCredentialRequest, SimulationSessionLeaseResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/heartbeat",
            new SimulationSessionCredentialRequest(workerRuntimeId, sessionToken),
            IsValidSimulationSessionLease,
            cancellationToken);
    }

    public Task<AuthServiceResult<SimulationSessionLeaseResponse>> ReleaseSimulationSessionAsync(
        Guid simulationSessionId,
        string workerRuntimeId,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationSessionCredentialRequest, SimulationSessionLeaseResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/release",
            new SimulationSessionCredentialRequest(workerRuntimeId, sessionToken),
            IsValidSimulationSessionLease,
            cancellationToken);
    }

    public Task<AuthServiceResult<SimulationItemTransactionResponse>>
        MutateSimulationItemsAsync(
            Guid simulationSessionId,
            SimulationItemOperationRequest request,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationItemOperationRequest, SimulationItemTransactionResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/item-operations",
            request,
            response => IsValidSimulationItemTransaction(response, request),
            cancellationToken);
    }

    private async Task<AuthServiceResult<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest requestBody,
        Func<TResponse, bool> responseValidator,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                path,
                requestBody,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await ReadJsonAsync<TResponse>(response, cancellationToken);
                return responseBody is not null && responseValidator(responseBody)
                    ? AuthServiceResult<TResponse>.Success(responseBody)
                    : InvalidResponse<TResponse>();
            }

            var problem = await ReadJsonAsync<AuthServiceProblemDetails>(response, cancellationToken);
            if (problem is null || string.IsNullOrWhiteSpace(problem.Code))
            {
                return InvalidResponse<TResponse>();
            }

            if (string.Equals(
                    problem.Code,
                    "invalid_service_credentials",
                    StringComparison.Ordinal))
            {
                return AuthServiceResult<TResponse>.Failure(
                    (int)HttpStatusCode.BadGateway,
                    "auth_service_authentication_failed",
                    "Simulation worker could not authenticate with AuthService.");
            }

            return AuthServiceResult<TResponse>.Failure(
                (int)response.StatusCode,
                problem.Code,
                problem.Detail ?? problem.Message ?? "AuthService rejected the request.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return AuthServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.GatewayTimeout,
                "auth_service_timeout",
                "AuthService did not respond before the timeout.");
        }
        catch (HttpRequestException)
        {
            return AuthServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.ServiceUnavailable,
                "auth_service_unavailable",
                "AuthService could not be reached.");
        }
        catch (JsonException)
        {
            return InvalidResponse<TResponse>();
        }
        catch (NotSupportedException)
        {
            return InvalidResponse<TResponse>();
        }
    }

    private async Task<AuthServiceResult<TResponse>> GetAsync<TResponse>(
        string path,
        Func<TResponse, bool> responseValidator,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await ReadJsonAsync<TResponse>(response, cancellationToken);
                return responseBody is not null && responseValidator(responseBody)
                    ? AuthServiceResult<TResponse>.Success(responseBody)
                    : InvalidResponse<TResponse>();
            }

            var problem = await ReadJsonAsync<AuthServiceProblemDetails>(response, cancellationToken);
            if (problem is null || string.IsNullOrWhiteSpace(problem.Code))
            {
                return InvalidResponse<TResponse>();
            }

            if (string.Equals(
                    problem.Code,
                    "invalid_service_credentials",
                    StringComparison.Ordinal))
            {
                return AuthServiceResult<TResponse>.Failure(
                    (int)HttpStatusCode.BadGateway,
                    "auth_service_authentication_failed",
                    "Simulation worker could not authenticate with AuthService.");
            }

            return AuthServiceResult<TResponse>.Failure(
                (int)response.StatusCode,
                problem.Code,
                problem.Detail ?? problem.Message ?? "AuthService rejected the request.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return AuthServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.GatewayTimeout,
                "auth_service_timeout",
                "AuthService did not respond before the timeout.");
        }
        catch (HttpRequestException)
        {
            return AuthServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.ServiceUnavailable,
                "auth_service_unavailable",
                "AuthService could not be reached.");
        }
        catch (JsonException)
        {
            return InvalidResponse<TResponse>();
        }
        catch (NotSupportedException)
        {
            return InvalidResponse<TResponse>();
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private static AuthServiceResult<T> InvalidResponse<T>()
    {
        return AuthServiceResult<T>.Failure(
            (int)HttpStatusCode.BadGateway,
            "invalid_auth_response",
            "AuthService returned an invalid response.");
    }

    private static bool IsValidConsumedTicket(
        ConsumedSimulationJoinTicketResponse response,
        string workerId,
        string runtimeId,
        string shardId)
    {
        return response.AccountId != Guid.Empty
            && response.CharacterId != Guid.Empty
            && response.SimulationSessionId != Guid.Empty
            && !string.IsNullOrWhiteSpace(response.CharacterName)
            && string.Equals(response.ShardId, shardId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(response.WorldId)
            && string.Equals(response.WorkerId, workerId, StringComparison.Ordinal)
            && string.Equals(response.WorkerRuntimeId, runtimeId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(response.SimulationSessionToken)
            && response.SessionExpiresAt != default
            && IsValidCarryState(
                response.ItemStateRevision,
                response.CarriedWeight,
                response.CarryCapacity);
    }

    private static bool IsValidSimulationSessionLease(
        SimulationSessionLeaseResponse response)
    {
        return response.SimulationSessionId != Guid.Empty
            && response.CharacterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(response.ShardId)
            && !string.IsNullOrWhiteSpace(response.WorkerId)
            && !string.IsNullOrWhiteSpace(response.WorkerRuntimeId)
            && response.ExpiresAt != default
            && IsValidCarryState(
                response.ItemStateRevision,
                response.CarriedWeight,
                response.CarryCapacity);
    }

    private static bool IsValidSimulationItemTransaction(
        SimulationItemTransactionResponse response,
        SimulationItemOperationRequest request)
    {
        if (response.OperationId != request.OperationId
            || !string.Equals(
                response.OperationKind,
                request.OperationKind,
                StringComparison.Ordinal)
            || !response.Succeeded
            || response.Error is not null
            || response.CharacterRevisions is null
            || response.ContainerRevisions is null
            || response.ItemRevisions is null
            || response.RecoveryDeliveryIds is null
            || response.CharacterRevisions.Count != 1
            || response.ContainerRevisions.Count > 8
            || response.ItemRevisions.Count > 32
            || response.RecoveryDeliveryIds.Count > 8
            || response.SecureContainerEntitlementRevision is not null)
        {
            return false;
        }

        var character = response.CharacterRevisions[0];
        return character.CharacterId == request.CharacterId
            && character.Revision >= request.ExpectedCharacterRevision
            && IsValidCarryState(
                character.Revision,
                character.CarriedWeight,
                character.CarryCapacity)
            && response.ContainerRevisions.All(
                revision => revision.ContainerId != Guid.Empty && revision.Revision >= 0)
            && response.ItemRevisions.All(
                revision => revision.ItemInstanceId != Guid.Empty && revision.Revision >= 0)
            && response.RecoveryDeliveryIds.All(deliveryId => deliveryId != Guid.Empty);
    }

    private static bool IsValidCorpseRestore(
        CorpseRestoreResponse response,
        string expectedShardId)
    {
        if (response.DatabaseTime == default
            || response.Corpses is null
            || response.Corpses.Count > MaximumRestoredCorpses)
        {
            return false;
        }

        var corpseIds = new HashSet<Guid>();
        foreach (var corpse in response.Corpses)
        {
            if (!corpseIds.Add(corpse.CorpseId)
                || !IsValidDurableCorpse(corpse, expectedShardId, response.DatabaseTime))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPlayerDeathPartition(
        PlayerDeathPartitionResponse response,
        SimulationPlayerDeathRequest request)
    {
        return response.OperationId == request.OperationId
            && response.DeathEventId == request.DeathEventId
            && response.Corpse.SourceCharacterId == request.CharacterId
            && response.Corpse.ExpiresAt - response.Corpse.CreatedAt == TimeSpan.FromMinutes(5)
            && IsValidDurableCorpse(response.Corpse, request.ShardId, null)
            && response.CharacterRevision.CharacterId == request.CharacterId
            && response.CharacterRevision.Revision >= request.ExpectedCharacterRevision
            && IsValidCarryValues(
                response.CharacterRevision.Revision,
                response.CharacterRevision.CarriedWeight,
                response.CharacterRevision.CarryCapacity)
            && response.RecoveryDeliveryIds is not null
            && response.RecoveryDeliveryIds.Count <= 8
            && response.RecoveryDeliveryIds.All(deliveryId => deliveryId != Guid.Empty)
            && response.RecoveryDeliveryIds.Distinct().Count()
                == response.RecoveryDeliveryIds.Count;
    }

    private static bool IsValidDurableCorpse(
        DurableCorpseResponse corpse,
        string expectedShardId,
        DateTime? minimumExpiry)
    {
        if (corpse.CorpseId == Guid.Empty
            || corpse.SourceCharacterId == Guid.Empty
            || string.IsNullOrWhiteSpace(corpse.SourceDisplayName)
            || !string.Equals(corpse.ShardId, expectedShardId, StringComparison.Ordinal)
            || !IsFinitePosition(corpse.PositionX)
            || !IsFinitePosition(corpse.PositionY)
            || !IsFinitePosition(corpse.PositionZ)
            || !IsNormalizedRotation(corpse)
            || !string.Equals(
                corpse.PresentationKey,
                "corpse.generic_loot_crate",
                StringComparison.Ordinal)
            || corpse.Revision < 0
            || corpse.CreatedAt == default
            || corpse.ExpiresAt <= corpse.CreatedAt
            || minimumExpiry is not null && corpse.ExpiresAt <= minimumExpiry.Value
            || corpse.Sections is null
            || corpse.Sections.Count != 3)
        {
            return false;
        }

        var sectionKinds = new HashSet<string>(StringComparer.Ordinal);
        var itemCount = 0L;
        foreach (var section in corpse.Sections)
        {
            if (!sectionKinds.Add(section.SectionKind)
                || section.SectionKind is not ("general_inventory" or "equipment" or "bag")
                || section.ContainerId == Guid.Empty
                || section.ContainerRevision < 0
                || section.ItemCount < 0)
            {
                return false;
            }

            itemCount += section.ItemCount;
        }

        return sectionKinds.Count == 3 && corpse.IsEmpty == (itemCount == 0);
    }

    private static bool IsFinitePosition(double value)
    {
        return double.IsFinite(value) && value is >= -1_000_000d and <= 1_000_000d;
    }

    private static bool IsNormalizedRotation(DurableCorpseResponse corpse)
    {
        var lengthSquared = (corpse.RotationX * corpse.RotationX)
            + (corpse.RotationY * corpse.RotationY)
            + (corpse.RotationZ * corpse.RotationZ)
            + (corpse.RotationW * corpse.RotationW);
        return double.IsFinite(lengthSquared)
            && Math.Abs(lengthSquared - 1d) <= 0.0001d
            && Math.Abs(corpse.RotationX) <= 1d
            && Math.Abs(corpse.RotationY) <= 1d
            && Math.Abs(corpse.RotationZ) <= 1d
            && Math.Abs(corpse.RotationW) <= 1d;
    }

    private static bool IsValidCarryState(
        long itemStateRevision,
        long carriedWeight,
        long carryCapacity)
    {
        return IsValidCarryValues(itemStateRevision, carriedWeight, carryCapacity)
            && PlayerEncumbranceRules.IsWithinHardCap(carriedWeight, carryCapacity);
    }

    private static bool IsValidCarryValues(
        long itemStateRevision,
        long carriedWeight,
        long carryCapacity)
    {
        return itemStateRevision >= 0
            && carriedWeight >= 0
            && carryCapacity > 0;
    }
}
