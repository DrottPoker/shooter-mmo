using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShooterMmo.GameSimulation;
using ShooterMmo.Shared.Health;

namespace SimulationWorker.Auth;

public sealed class AuthServiceClient(HttpClient httpClient)
{
    private const int MaximumRestoredCorpses = 4_096;
    private const int MaximumCorpseEquipmentSlotIdLength = 64;

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

    public Task<AuthServiceResult<PersistentMobCorpseResponse>>
        CreatePersistentMobCorpseAsync(
            CreatePersistentMobCorpseRequest request,
            CancellationToken cancellationToken)
    {
        return PostAsync<CreatePersistentMobCorpseRequest, PersistentMobCorpseResponse>(
            $"/api/simulation-workers/{Uri.EscapeDataString(request.WorkerId)}/mob-corpses/durable",
            request,
            response => response.OperationId == request.OperationId
                && response.Corpse is not null
                && response.Corpse.CorpseId == request.CorpseId
                && response.Corpse.SourceCharacterId is null
                && string.Equals(
                    response.Corpse.SourceDisplayName,
                    request.SourceDisplayName.Trim(),
                    StringComparison.Ordinal)
                && Math.Abs(((response.Corpse.ExpiresAt - response.Corpse.CreatedAt)
                        - TimeSpan.FromSeconds(request.LifetimeSeconds)).TotalMilliseconds)
                    <= 1d
                && IsValidDurableCorpse(response.Corpse, request.ShardId, null),
            cancellationToken);
    }

    public Task<AuthServiceResult<SimulationItemTransactionResponse>> GrantLiveMobLootAsync(
        Guid simulationSessionId,
        SimulationMobLootGrantRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationMobLootGrantRequest, SimulationItemTransactionResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/mob-loot-grants",
            request,
            response => IsValidLiveMobLootGrant(response, request),
            cancellationToken);
    }

    public Task<AuthServiceResult<CorpseViewSnapshotResponse>> OpenCorpseAsync(
        Guid simulationSessionId,
        Guid corpseId,
        SimulationCorpseOpenRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationCorpseOpenRequest, CorpseViewSnapshotResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/corpses/{corpseId}/open",
            request,
            response => IsValidCorpseView(response, corpseId, request.ShardId),
            cancellationToken);
    }

    public Task<AuthServiceResult<CorpseMutationResponse>> MutateCorpseAsync(
        Guid simulationSessionId,
        Guid corpseId,
        SimulationCorpseMutationRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationCorpseMutationRequest, CorpseMutationResponse>(
            $"/api/simulation-sessions/{simulationSessionId}/corpses/{corpseId}/item-operations",
            request,
            response => IsValidCorpseMutation(response, request, corpseId),
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
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.TryAddWithoutValidation(
                "X-Correlation-ID",
                CreateCorrelationId());
            using var response = await httpClient.SendAsync(request, cancellationToken);

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
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(
                "X-Correlation-ID",
                CreateCorrelationId());
            using var response = await httpClient.SendAsync(request, cancellationToken);
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

    private static string CreateCorrelationId()
    {
        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
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
            || response.CharacterRevisions.Count > 1
            || response.ContainerRevisions.Count > 8
            || response.ItemRevisions.Count > 32
            || response.RecoveryDeliveryIds.Count > 8
            || response.SecureContainerEntitlementRevision is not null)
        {
            return false;
        }

        var characterValid = response.CharacterRevisions.Count == 0
            ? request.OperationKind is "grant" or "abandon_quest_items"
            : response.CharacterRevisions[0].CharacterId == request.CharacterId
                && response.CharacterRevisions[0].Revision
                    >= request.ExpectedCharacterRevision
                && IsValidCarryState(
                    response.CharacterRevisions[0].Revision,
                    response.CharacterRevisions[0].CarriedWeight,
                    response.CharacterRevisions[0].CarryCapacity);
        return characterValid
            && response.ContainerRevisions.All(
                revision => revision.ContainerId != Guid.Empty && revision.Revision >= 0)
            && response.ItemRevisions.All(
                revision => revision.ItemInstanceId != Guid.Empty && revision.Revision >= 0)
            && response.RecoveryDeliveryIds.All(deliveryId => deliveryId != Guid.Empty);
    }

    private static bool IsValidCorpseMutation(
        CorpseMutationResponse response,
        SimulationCorpseMutationRequest request,
        Guid corpseId)
    {
        var isInternalMove = request.OperationKind is "move_item" or "move_partial_stack";
        var expectedCharacterRevisionCount = isInternalMove ? 0 : 1;
        if (response.Transaction is null
            || response.Transaction.OperationId != request.OperationId
            || !response.Transaction.Succeeded
            || response.Transaction.Error is not null
            || response.Transaction.CharacterRevisions is null
            || response.Transaction.CharacterRevisions.Count != expectedCharacterRevisionCount
            || (!isInternalMove
                && response.Transaction.CharacterRevisions[0].CharacterId != request.CharacterId)
            || response.Transaction.ContainerRevisions is null
            || response.Transaction.ContainerRevisions.Count > 8
            || response.Transaction.ItemRevisions is null
            || response.Transaction.ItemRevisions.Count > 32
            || response.Transaction.RecoveryDeliveryIds is null
            || response.Transaction.RecoveryDeliveryIds.Count != 0
            || response.Transaction.SecureContainerEntitlementRevision is not null)
        {
            return false;
        }

        var expectedOperationKind = request.OperationKind switch
        {
            "loot_item" => "loot_corpse_item",
            "loot_partial_stack" => "loot_corpse_partial_stack",
            "deposit_item" => "deposit_corpse_item",
            "deposit_partial_stack" => "deposit_corpse_partial_stack",
            "move_item" => "move_corpse_item",
            "move_partial_stack" => "move_corpse_partial_stack",
            "swap_bag" => "swap_corpse_bag",
            _ => string.Empty
        };
        return string.Equals(
                response.Transaction.OperationKind,
                expectedOperationKind,
                StringComparison.Ordinal)
            && (isInternalMove
                || IsValidCarryState(
                    response.Transaction.CharacterRevisions[0].Revision,
                    response.Transaction.CharacterRevisions[0].CarriedWeight,
                    response.Transaction.CharacterRevisions[0].CarryCapacity))
            && response.Transaction.ContainerRevisions.All(
                revision => revision.ContainerId != Guid.Empty && revision.Revision >= 0)
            && response.Transaction.ItemRevisions.All(
                revision => revision.ItemInstanceId != Guid.Empty && revision.Revision >= 0)
            && (response.Corpse is null
                || IsValidCorpseView(response.Corpse, corpseId, request.ShardId));
    }

    private static bool IsValidLiveMobLootGrant(
        SimulationItemTransactionResponse response,
        SimulationMobLootGrantRequest request)
    {
        return response.OperationId == request.GrantId
            && string.Equals(response.OperationKind, "grant_mob_loot", StringComparison.Ordinal)
            && response.Succeeded
            && response.Error is null
            && response.CharacterRevisions is not null
            && response.CharacterRevisions.Count == 1
            && response.CharacterRevisions[0].CharacterId == request.CharacterId
            && IsValidCarryState(
                response.CharacterRevisions[0].Revision,
                response.CharacterRevisions[0].CarriedWeight,
                response.CharacterRevisions[0].CarryCapacity)
            && response.ContainerRevisions is not null
            && response.ContainerRevisions.Count == 1
            && response.ContainerRevisions[0].ContainerId == request.DestinationContainerId
            && response.ContainerRevisions[0].Revision
                > request.ExpectedDestinationContainerRevision
            && response.ItemRevisions is not null
            && response.ItemRevisions.Count == 1
            && response.ItemRevisions[0].ItemInstanceId != Guid.Empty
            && response.ItemRevisions[0].Revision >= 0
            && response.RecoveryDeliveryIds is not null
            && response.RecoveryDeliveryIds.Count == 0
            && response.SecureContainerEntitlementRevision is null;
    }

    private static bool IsValidCorpseView(
        CorpseViewSnapshotResponse response,
        Guid expectedCorpseId,
        string expectedShardId)
    {
        if (response is null
            || response.CorpseId != expectedCorpseId
            || response.SourceCharacterId == Guid.Empty
            || string.IsNullOrWhiteSpace(response.SourceDisplayName)
            || !string.Equals(response.ShardId, expectedShardId, StringComparison.Ordinal)
            || !IsFinitePosition(response.PositionX)
            || !IsFinitePosition(response.PositionY)
            || !IsFinitePosition(response.PositionZ)
            || string.IsNullOrWhiteSpace(response.PresentationKey)
            || response.Revision < 0
            || response.CreatedAt == default
            || response.ExpiresAt <= response.CreatedAt
            || response.Sections is null
            || response.Sections.Count != 3
            || response.PresentationSnapshots is null)
        {
            return false;
        }

        var sectionKinds = new HashSet<string>(StringComparer.Ordinal);
        var itemIds = new HashSet<Guid>();
        foreach (var section in response.Sections)
        {
            if (!sectionKinds.Add(section.SectionKind)
                || section.SectionKind is not ("general_inventory" or "equipment" or "bag")
                || section.ContainerId == Guid.Empty
                || string.IsNullOrWhiteSpace(section.ContainerType)
                || section.ContainerRevision < 0
                || section.SlotCapacity <= 0
                || section.SlotCapacity > 512
                || section.Slots is null
                || section.Slots.Count != section.SlotCapacity)
            {
                return false;
            }

            var slotIndexes = new HashSet<int>();
            var equipmentSlotIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in section.Slots)
            {
                var isEquipmentSlot = section.SectionKind == "equipment";
                if (!slotIndexes.Add(slot.SlotIndex)
                    || slot.SlotIndex < 0
                    || slot.SlotIndex >= section.SlotCapacity
                    || string.IsNullOrWhiteSpace(slot.SlotKind)
                    || slot.AcceptedTags is null
                    || isEquipmentSlot != !string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    || (slot.EquipmentSlotId?.Length ?? 0)
                        > MaximumCorpseEquipmentSlotIdLength
                    || (isEquipmentSlot && !equipmentSlotIds.Add(slot.EquipmentSlotId!)))
                {
                    return false;
                }

                if (slot.Item is null)
                {
                    continue;
                }

                if (!itemIds.Add(slot.Item.ItemInstanceId)
                    || slot.Item.ItemInstanceId == Guid.Empty
                    || string.IsNullOrWhiteSpace(slot.Item.DefinitionId)
                    || slot.Item.Quantity <= 0
                    || slot.Item.Revision < 0
                    || (slot.Item.BagContentsContainerId is null)
                        != (slot.Item.BagContentsRevision is null)
                    || slot.Item.BagContentsRevision < 0)
                {
                    return false;
                }
            }
        }

        return sectionKinds.Count == 3;
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
