using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShooterMmo.Shared.Health;

namespace SimulationWorker.Auth;

public sealed class AuthServiceClient(HttpClient httpClient)
{
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
            && response.SessionExpiresAt != default;
    }

    private static bool IsValidSimulationSessionLease(
        SimulationSessionLeaseResponse response)
    {
        return response.SimulationSessionId != Guid.Empty
            && response.CharacterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(response.ShardId)
            && !string.IsNullOrWhiteSpace(response.WorkerId)
            && !string.IsNullOrWhiteSpace(response.WorkerRuntimeId)
            && response.ExpiresAt != default;
    }
}
