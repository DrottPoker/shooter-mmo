using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Health;
using Mmorpg.Server.Common.Identity;
using Mmorpg.SimulationWorker.Telemetry;

namespace Mmorpg.SimulationWorker.GameServiceApi;

public sealed class GameServiceClient(HttpClient httpClient)
{
    private const int MaximumRestoredCorpses = 4_096;
    // A death creates at most one Recovery delivery per protection source. The bound only
    // rejects malformed responses.
    private const int MaximumDeathRecoveryDeliveries = 8;
    private const int MaximumForwardedErrorCodeLength = 64;
    private const int MaximumForwardedErrorMessageLength = 256;

    // One player item or corpse operation touches only a few containers and items, so these
    // bounds only reject malformed responses.
    private const int MaximumOperationContainerRevisions = 8;
    private const int MaximumOperationItemRevisions = 32;
    // A sanity bound for Loot all, which moves every corpse item and tops up as many carried
    // stacks: a player corpse holds a whole inventory, equipment, and Bag.
    private const int MaximumLootAllItemRevisions = 512;
    private const int MaximumOperationRecoveryDeliveries = 8;

    // A character has one item per equipment slot, and the catalog has far fewer slots.
    private const int MaximumEquippedItems = 64;

    public async Task<DependencyHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/health/ready", cancellationToken);
            return new DependencyHealth(
                "game-service",
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
                "game-service",
                httpClient.BaseAddress!.ToString(),
                false,
                "Request timed out.");
        }
        catch (HttpRequestException)
        {
            return new DependencyHealth(
                "game-service",
                httpClient.BaseAddress!.ToString(),
                false,
                "Connection failed.");
        }
    }

    public Task<GameServiceResult<SimulationWorkerHeartbeatResponse>>
        HeartbeatSimulationWorkerAsync(
            string workerId,
            SimulationWorkerHeartbeatRequest request,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationWorkerHeartbeatRequest, SimulationWorkerHeartbeatResponse>(
            "GameService.HeartbeatSimulationWorker",
            $"/api/simulation-workers/{Uri.EscapeDataString(workerId)}/heartbeat",
            request,
            response => string.Equals(response.WorkerId, workerId, StringComparison.Ordinal)
                && string.Equals(response.RuntimeId, request.RuntimeId, StringComparison.Ordinal)
                && string.Equals(response.FleetId, request.FleetId, StringComparison.Ordinal)
                && string.Equals(response.NodeId, request.NodeId, StringComparison.Ordinal)
                && string.Equals(response.ShardId, request.ShardId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(response.WorldId)
                && !string.IsNullOrWhiteSpace(response.RuleSet)
                && response.RuleSet.Length <= 128
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
                && string.Equals(
                    response.ItemCatalogRevision,
                    request.ItemCatalogRevision,
                    StringComparison.Ordinal)
                && response.LastHeartbeatAt != default
                && response.OnlineUntil > response.LastHeartbeatAt,
            cancellationToken);
    }

    public Task<GameServiceResult<SimulationWorkerOfflineResponse>>
        MarkSimulationWorkerOfflineAsync(
            string workerId,
            string runtimeId,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationWorkerOfflineRequest, SimulationWorkerOfflineResponse>(
            "GameService.MarkSimulationWorkerOffline",
            $"/api/simulation-workers/{Uri.EscapeDataString(workerId)}/offline",
            new SimulationWorkerOfflineRequest(runtimeId),
            response => string.Equals(response.WorkerId, workerId, StringComparison.Ordinal)
                && string.Equals(response.RuntimeId, runtimeId, StringComparison.Ordinal)
                && response.OfflineAt != default,
            cancellationToken);
    }

    public Task<GameServiceResult<CorpseRestoreResponse>> RestoreDurableCorpsesAsync(
        string workerId,
        string workerRuntimeId,
        string shardId,
        CancellationToken cancellationToken)
    {
        var path = $"/api/simulation-workers/{Uri.EscapeDataString(workerId)}/corpses"
            + $"?workerRuntimeId={Uri.EscapeDataString(workerRuntimeId)}"
            + $"&shardId={Uri.EscapeDataString(shardId)}";
        return GetAsync<CorpseRestoreResponse>(
            "GameService.RestoreDurableCorpses",
            path,
            response => IsValidCorpseRestore(response, shardId),
            cancellationToken);
    }

    public Task<GameServiceResult<PersistentMobCorpseResponse>>
        CreatePersistentMobCorpseAsync(
            CreatePersistentMobCorpseRequest request,
            CancellationToken cancellationToken)
    {
        return PostAsync<CreatePersistentMobCorpseRequest, PersistentMobCorpseResponse>(
            "GameService.CreatePersistentMobCorpse",
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

    public Task<GameServiceResult<SimulationItemTransactionResponse>> GrantLiveMobLootAsync(
        Guid simulationSessionId,
        SimulationMobLootGrantRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationMobLootGrantRequest, SimulationItemTransactionResponse>(
            "GameService.GrantLiveMobLoot",
            $"/api/simulation-sessions/{simulationSessionId}/mob-loot-grants",
            request,
            response => IsValidLiveMobLootGrant(response, request),
            cancellationToken);
    }

    public Task<GameServiceResult<SimulationMobLootAllResponse>> GrantLiveMobLootAllAsync(
        Guid simulationSessionId,
        SimulationMobLootAllRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationMobLootAllRequest, SimulationMobLootAllResponse>(
            "GameService.GrantLiveMobLootAll",
            $"/api/simulation-sessions/{simulationSessionId}/mob-loot-grants/loot-all",
            request,
            response => IsValidLiveMobLootAll(response, request),
            cancellationToken);
    }

    public Task<GameServiceResult<CorpseViewSnapshotResponse>> OpenCorpseAsync(
        Guid simulationSessionId,
        Guid corpseId,
        SimulationCorpseOpenRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationCorpseOpenRequest, CorpseViewSnapshotResponse>(
            "GameService.OpenCorpse",
            $"/api/simulation-sessions/{simulationSessionId}/corpses/{corpseId}/open",
            request,
            response => IsValidCorpseView(response, corpseId, request.ShardId),
            cancellationToken);
    }

    public Task<GameServiceResult<CorpseMutationResponse>> MutateCorpseAsync(
        Guid simulationSessionId,
        Guid corpseId,
        SimulationCorpseMutationRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationCorpseMutationRequest, CorpseMutationResponse>(
            "GameService.MutateCorpse",
            $"/api/simulation-sessions/{simulationSessionId}/corpses/{corpseId}/item-operations",
            request,
            response => IsValidCorpseMutation(response, request, corpseId),
            cancellationToken);
    }

    public Task<GameServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeJoinTicketAsync(
        string ticket,
        string workerId,
        string runtimeId,
        string shardId,
        CancellationToken cancellationToken)
    {
        return PostAsync<ConsumeSimulationJoinTicketRequest, ConsumedSimulationJoinTicketResponse>(
            "GameService.ConsumeJoinTicket",
            "/api/simulation-join-tickets/consume",
            new ConsumeSimulationJoinTicketRequest(ticket, workerId, runtimeId, shardId),
            response => IsValidConsumedTicket(response, workerId, runtimeId, shardId),
            cancellationToken);
    }

    public Task<GameServiceResult<PlayerDeathPartitionResponse>> ProcessPlayerDeathAsync(
        Guid simulationSessionId,
        SimulationPlayerDeathRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationPlayerDeathRequest, PlayerDeathPartitionResponse>(
            "GameService.ProcessPlayerDeath",
            $"/api/simulation-sessions/{simulationSessionId}/player-deaths",
            request,
            response => IsValidPlayerDeathPartition(response, request),
            cancellationToken);
    }

    public Task<GameServiceResult<SimulationSessionLeaseResponse>>
        HeartbeatSimulationSessionAsync(
            Guid simulationSessionId,
            string workerRuntimeId,
            string sessionToken,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationSessionCredentialRequest, SimulationSessionLeaseResponse>(
            "GameService.HeartbeatSimulationSession",
            $"/api/simulation-sessions/{simulationSessionId}/heartbeat",
            new SimulationSessionCredentialRequest(workerRuntimeId, sessionToken),
            IsValidSimulationSessionLease,
            cancellationToken);
    }

    // Lowers or raises a session's headwear, or in Development stores another body model and look.
    public Task<GameServiceResult<SimulationAppearance>> ChangeAppearanceAsync(
        Guid simulationSessionId,
        SimulationAppearanceChangeRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationAppearanceChangeRequest, SimulationAppearance>(
            "GameService.ChangeAppearance",
            $"/api/simulation-sessions/{simulationSessionId}/appearance",
            request,
            IsValidAppearance,
            cancellationToken);
    }

    public Task<GameServiceResult<SimulationSessionLeaseResponse>> ReleaseSimulationSessionAsync(
        Guid simulationSessionId,
        string workerRuntimeId,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationSessionCredentialRequest, SimulationSessionLeaseResponse>(
            "GameService.ReleaseSimulationSession",
            $"/api/simulation-sessions/{simulationSessionId}/release",
            new SimulationSessionCredentialRequest(workerRuntimeId, sessionToken),
            IsValidSimulationSessionLease,
            cancellationToken);
    }

    public Task<GameServiceResult<SimulationItemTransactionResponse>> ConsumeAmmunitionAsync(
        Guid simulationSessionId,
        SimulationAmmunitionConsumptionRequest request,
        CancellationToken cancellationToken)
    {
        return PostAsync<SimulationAmmunitionConsumptionRequest, SimulationItemTransactionResponse>(
            "GameService.ConsumeAmmunition",
            $"/api/simulation-sessions/{simulationSessionId}/ammunition-consumptions",
            request,
            response => IsValidAmmunitionConsumption(response, request),
            cancellationToken);
    }

    public Task<GameServiceResult<SimulationItemTransactionResponse>>
        MutateSimulationItemsAsync(
            Guid simulationSessionId,
            SimulationItemOperationRequest request,
            CancellationToken cancellationToken)
    {
        return PostAsync<SimulationItemOperationRequest, SimulationItemTransactionResponse>(
            "GameService.MutateSimulationItems",
            $"/api/simulation-sessions/{simulationSessionId}/item-operations",
            request,
            response => IsValidSimulationItemTransaction(response, request),
            cancellationToken);
    }

    // Each call is one span named after its operation. A failure code lands on that
    // span, and the HTTP client span and the GameService request nest below it.
    private async Task<GameServiceResult<TResponse>> PostAsync<TRequest, TResponse>(
        string operation,
        string path,
        TRequest requestBody,
        Func<TResponse, bool> responseValidator,
        CancellationToken cancellationToken)
    {
        using var activity = SimulationWorkerTelemetry.StartOperation(operation);
        return RecordOutcome(
            activity,
            await SendPostAsync(path, requestBody, responseValidator, cancellationToken));
    }

    private async Task<GameServiceResult<TResponse>> GetAsync<TResponse>(
        string operation,
        string path,
        Func<TResponse, bool> responseValidator,
        CancellationToken cancellationToken)
    {
        using var activity = SimulationWorkerTelemetry.StartOperation(operation);
        return RecordOutcome(
            activity,
            await SendGetAsync(path, responseValidator, cancellationToken));
    }

    private static GameServiceResult<T> RecordOutcome<T>(
        Activity? activity,
        GameServiceResult<T> result)
    {
        if (!result.Succeeded)
        {
            SimulationWorkerTelemetry.RecordFailure(activity, result.Error!.Code);
        }

        return result;
    }

    private async Task<GameServiceResult<TResponse>> SendPostAsync<TRequest, TResponse>(
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
                    ? GameServiceResult<TResponse>.Success(responseBody)
                    : InvalidResponse<TResponse>();
            }

            var problem = await ReadJsonAsync<GameServiceProblemDetails>(response, cancellationToken);
            if (problem is null || string.IsNullOrWhiteSpace(problem.Code))
            {
                return InvalidResponse<TResponse>();
            }

            if (string.Equals(
                    problem.Code,
                    "invalid_service_credentials",
                    StringComparison.Ordinal))
            {
                return GameServiceResult<TResponse>.Failure(
                    (int)HttpStatusCode.BadGateway,
                    "auth_service_authentication_failed",
                    "Simulation worker could not authenticate with GameService.");
            }

            return GameServiceResult<TResponse>.Failure(
                (int)response.StatusCode,
                NormalizeForwardedErrorCode(problem.Code),
                NormalizeForwardedErrorMessage(problem.Detail ?? problem.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return GameServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.GatewayTimeout,
                "auth_service_timeout",
                "GameService did not respond before the timeout.");
        }
        catch (HttpRequestException)
        {
            return GameServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.ServiceUnavailable,
                "auth_service_unavailable",
                "GameService could not be reached.");
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

    private async Task<GameServiceResult<TResponse>> SendGetAsync<TResponse>(
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
                    ? GameServiceResult<TResponse>.Success(responseBody)
                    : InvalidResponse<TResponse>();
            }

            var problem = await ReadJsonAsync<GameServiceProblemDetails>(response, cancellationToken);
            if (problem is null || string.IsNullOrWhiteSpace(problem.Code))
            {
                return InvalidResponse<TResponse>();
            }

            if (string.Equals(
                    problem.Code,
                    "invalid_service_credentials",
                    StringComparison.Ordinal))
            {
                return GameServiceResult<TResponse>.Failure(
                    (int)HttpStatusCode.BadGateway,
                    "auth_service_authentication_failed",
                    "Simulation worker could not authenticate with GameService.");
            }

            return GameServiceResult<TResponse>.Failure(
                (int)response.StatusCode,
                NormalizeForwardedErrorCode(problem.Code),
                NormalizeForwardedErrorMessage(problem.Detail ?? problem.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return GameServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.GatewayTimeout,
                "auth_service_timeout",
                "GameService did not respond before the timeout.");
        }
        catch (HttpRequestException)
        {
            return GameServiceResult<TResponse>.Failure(
                (int)HttpStatusCode.ServiceUnavailable,
                "auth_service_unavailable",
                "GameService could not be reached.");
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

    private static GameServiceResult<T> InvalidResponse<T>()
    {
        return GameServiceResult<T>.Failure(
            (int)HttpStatusCode.BadGateway,
            "invalid_auth_response",
            "GameService returned an invalid response.");
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
            && response.SessionLeaseDurationMilliseconds > 0
            && IsValidCarryState(
                response.ItemStateRevision,
                response.CarriedWeight,
                response.CarryCapacity)
            && IsValidEquipment(response.Equipment)
            // A person's character always has a look; a development bot is dressed by the worker.
            && (response.IsSyntheticBot ? response.Appearance is null : IsValidAppearance(response.Appearance));
    }

    public static bool IsValidAppearance(SimulationAppearance? appearance)
    {
        return appearance is not null
            && !string.IsNullOrWhiteSpace(appearance.BodyModel)
            && appearance.Choices is not null
            && appearance.Choices.All(choice => choice is not null
                && !string.IsNullOrWhiteSpace(choice.Kind)
                && choice.Slot is not null
                && !string.IsNullOrWhiteSpace(choice.Option));
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
            && (response.Released || response.LeaseDurationMilliseconds > 0)
            && IsValidCarryState(
                response.ItemStateRevision,
                response.CarriedWeight,
                response.CarryCapacity)
            && IsValidEquipment(response.Equipment);
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
            || response.ContainerRevisions.Count > MaximumOperationContainerRevisions
            || response.ItemRevisions.Count > MaximumOperationItemRevisions
            || response.RecoveryDeliveryIds.Count > MaximumOperationRecoveryDeliveries
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
            && response.RecoveryDeliveryIds.All(deliveryId => deliveryId != Guid.Empty)
            && IsValidCharacterState(
                response.CharacterState,
                request.CharacterId,
                response.CharacterRevisions.Count == 0 ? 0 : response.CharacterRevisions[0].Revision);
    }

    private static bool IsValidAmmunitionConsumption(
        SimulationItemTransactionResponse response,
        SimulationAmmunitionConsumptionRequest request)
    {
        return response.OperationId == request.OperationId
            && string.Equals(
                response.OperationKind,
                "consume_equipped_ammunition",
                StringComparison.Ordinal)
            && response.Succeeded
            && response.Error is null
            && response.CharacterRevisions is not null
            && response.CharacterRevisions.Count == 1
            && response.CharacterRevisions[0].CharacterId == request.CharacterId
            && response.CharacterRevisions[0].Revision > request.ExpectedCharacterRevision
            && IsValidCarryState(
                response.CharacterRevisions[0].Revision,
                response.CharacterRevisions[0].CarriedWeight,
                response.CharacterRevisions[0].CarryCapacity)
            && response.ContainerRevisions is not null
            && response.ContainerRevisions.Count == 0
            && response.ItemRevisions is not null
            && response.ItemRevisions.Count <= 1
            && response.ItemRevisions.All(revision =>
                revision.ItemInstanceId == request.ItemInstanceId
                && revision.Revision > request.ExpectedItemRevision)
            && response.RecoveryDeliveryIds is not null
            && response.RecoveryDeliveryIds.Count == 0
            && response.SecureContainerEntitlementRevision is null
            && IsValidCharacterState(
                response.CharacterState,
                request.CharacterId,
                response.CharacterRevisions[0].Revision);
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
            || response.Transaction.ContainerRevisions.Count > MaximumOperationContainerRevisions
            || response.Transaction.ItemRevisions is null
            || response.Transaction.ItemRevisions.Count > (request.OperationKind == "loot_all"
                ? MaximumLootAllItemRevisions
                : MaximumOperationItemRevisions)
            || response.Transaction.RecoveryDeliveryIds is null
            || response.Transaction.RecoveryDeliveryIds.Count != 0
            || response.Transaction.SecureContainerEntitlementRevision is not null)
        {
            return false;
        }

        var expectedOperationKind = request.OperationKind switch
        {
            "loot_all" => "loot_all_corpse_items",
            "loot_item" => "loot_corpse_item",
            "loot_partial_stack" => "loot_corpse_partial_stack",
            "loot_item_to_equipment" => "loot_corpse_item_to_equipment",
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
                || IsValidCorpseView(response.Corpse, corpseId, request.ShardId))
            && IsValidCharacterState(
                response.CharacterState,
                request.CharacterId,
                isInternalMove ? 0 : response.Transaction.CharacterRevisions[0].Revision);
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
            // A grant into an equipment slot touches no container.
            && (request.EquipmentSlotId is null
                ? response.ContainerRevisions.Count == 1
                    && response.ContainerRevisions[0].ContainerId == request.DestinationContainerId
                    && response.ContainerRevisions[0].Revision
                        > request.ExpectedDestinationContainerRevision
                : response.ContainerRevisions.Count == 0)
            && response.ItemRevisions is not null
            && response.ItemRevisions.Count == 1
            && response.ItemRevisions[0].ItemInstanceId != Guid.Empty
            && response.ItemRevisions[0].Revision >= 0
            && response.RecoveryDeliveryIds is not null
            && response.RecoveryDeliveryIds.Count == 0
            && response.SecureContainerEntitlementRevision is null
            && IsValidCharacterState(
                response.CharacterState,
                request.CharacterId,
                response.CharacterRevisions[0].Revision);
    }

    // Every granted id names an entry of the request, and the transaction belongs to the
    // character. Loot all may touch every slot of the inventory and the Bag.
    private static bool IsValidLiveMobLootAll(
        SimulationMobLootAllResponse response,
        SimulationMobLootAllRequest request)
    {
        var transaction = response?.Transaction;
        var requested = request.Loot.Select(loot => loot.GrantId).ToHashSet();
        return transaction is not null
            && transaction.OperationId == request.OperationId
            && string.Equals(transaction.OperationKind, "grant_mob_loot_all", StringComparison.Ordinal)
            && transaction.Succeeded
            && transaction.Error is null
            && transaction.CharacterRevisions is { Count: 1 }
            && transaction.CharacterRevisions[0].CharacterId == request.CharacterId
            && IsValidCarryState(
                transaction.CharacterRevisions[0].Revision,
                transaction.CharacterRevisions[0].CarriedWeight,
                transaction.CharacterRevisions[0].CarryCapacity)
            && transaction.ContainerRevisions is not null
            && transaction.ContainerRevisions.Count <= MaximumOperationContainerRevisions
            && transaction.ContainerRevisions.All(revision => revision.ContainerId != Guid.Empty && revision.Revision >= 0)
            && transaction.ItemRevisions is not null
            && transaction.ItemRevisions.Count <= MaximumLootAllItemRevisions
            && transaction.ItemRevisions.All(revision => revision.ItemInstanceId != Guid.Empty && revision.Revision >= 0)
            && transaction.RecoveryDeliveryIds is { Count: 0 }
            && transaction.SecureContainerEntitlementRevision is null
            && response!.GrantedGrantIds is not null
            && response.GrantedGrantIds.Distinct().Count() == response.GrantedGrantIds.Count
            && response.GrantedGrantIds.All(requested.Contains)
            && IsValidCharacterState(
                transaction.CharacterState,
                request.CharacterId,
                transaction.CharacterRevisions[0].Revision);
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
                || section.SlotCapacity < 0
                || section.SlotCapacity > RealtimeProtocolLimits.MaximumCorpseSectionSlotCapacity
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
                        > RealtimeProtocolLimits.MaximumEquipmentSlotIdLength
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
            && IsValidDurableCorpse(response.Corpse, request.ShardId, null)
            && response.CharacterRevision.CharacterId == request.CharacterId
            && response.CharacterRevision.Revision >= request.ExpectedCharacterRevision
            && IsValidCarryState(
                response.CharacterRevision.Revision,
                response.CharacterRevision.CarriedWeight,
                response.CharacterRevision.CarryCapacity)
            && response.RecoveryDeliveryIds is not null
            && response.RecoveryDeliveryIds.Count <= MaximumDeathRecoveryDeliveries
            && response.RecoveryDeliveryIds.All(deliveryId => deliveryId != Guid.Empty)
            && response.RecoveryDeliveryIds.Distinct().Count()
                == response.RecoveryDeliveryIds.Count
            && IsValidCharacterState(
                response.CharacterState,
                request.CharacterId,
                response.CharacterRevision.Revision);
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
            || string.IsNullOrWhiteSpace(corpse.PresentationKey)
            || corpse.PresentationKey.Length
                > RealtimeProtocolLimits.MaximumCorpsePresentationKeyLength
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

    // GameService error text is forwarded into length-limited realtime protocol fields,
    // so only short printable ASCII passes and anything else becomes a stable default.
    internal static string NormalizeForwardedErrorCode(string code)
    {
        var trimmed = code.Trim();
        return trimmed.Length is > 0 and <= MaximumForwardedErrorCodeLength
            && trimmed.All(character => character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_')
            ? trimmed
            : "auth_service_rejected";
    }

    internal static string NormalizeForwardedErrorMessage(string? message)
    {
        var printable = new string((message ?? string.Empty)
            .Select(character => character is >= ' ' and <= '~' ? character : '?')
            .ToArray())
            .Trim();
        if (printable.Length == 0)
        {
            return "GameService rejected the request.";
        }

        return printable.Length <= MaximumForwardedErrorMessageLength
            ? printable
            : printable[..MaximumForwardedErrorMessageLength];
    }

    // Every mutation response carries the acting character's state, read after the commit.
    // A later operation may have committed in between, so the state is never older than
    // the committed revision but can be newer.
    private static bool IsValidCharacterState(
        SimulationCharacterState? state,
        Guid characterId,
        long committedRevision)
    {
        return state is not null
            && state.CharacterId == characterId
            && state.ItemStateRevision >= committedRevision
            && IsValidCarryState(state.ItemStateRevision, state.CarriedWeight, state.CarryCapacity)
            && IsValidEquipment(state.Equipment);
    }

    private static bool IsValidEquipment(IReadOnlyList<SimulationEquippedItem>? equipment)
    {
        return equipment is not null
            && equipment.Count <= MaximumEquippedItems
            && equipment.All(item =>
                item is not null
                && Identifiers.IsContentIdentifier(item.EquipmentSlotId)
                && item.ItemInstanceId != Guid.Empty
                && Identifiers.IsContentIdentifier(item.DefinitionId)
                && item.Quantity > 0
                && item.Revision >= 0)
            && equipment.Select(item => item.EquipmentSlotId).Distinct(StringComparer.Ordinal).Count()
                == equipment.Count
            && equipment.Select(item => item.ItemInstanceId).Distinct().Count() == equipment.Count;
    }

    // Weight above the hard cap is valid: death can leave an involuntary overflow.
    private static bool IsValidCarryState(
        long itemStateRevision,
        long carriedWeight,
        long carryCapacity)
    {
        return itemStateRevision >= 0
            && carriedWeight >= 0
            && carryCapacity > 0;
    }
}
