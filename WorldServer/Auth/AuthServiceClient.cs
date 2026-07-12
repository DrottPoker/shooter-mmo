using System.Net.Http.Json;
using System.Text.Json;
using ShooterMmo.Shared.Health;

namespace WorldServer.Auth;

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

    public Task<AuthServiceResult<WorldHeartbeatResponse>> HeartbeatWorldAsync(
        string worldId,
        CancellationToken cancellationToken)
    {
        return PostAsync<object, WorldHeartbeatResponse>(
            $"/api/worlds/{Uri.EscapeDataString(worldId)}/heartbeat",
            new { },
            response => string.Equals(response.WorldId, worldId, StringComparison.Ordinal)
                && response.LastHeartbeatAt != default
                && response.OnlineUntil > response.LastHeartbeatAt,
            cancellationToken);
    }

    public Task<AuthServiceResult<ConsumedJoinTicketResponse>> ConsumeJoinTicketAsync(
        string ticket,
        string worldId,
        CancellationToken cancellationToken)
    {
        return PostAsync<ConsumeJoinTicketRequest, ConsumedJoinTicketResponse>(
            "/api/world-join-tickets/consume",
            new ConsumeJoinTicketRequest(ticket, worldId),
            IsValidConsumedTicket,
            cancellationToken);
    }

    public Task<AuthServiceResult<WorldSessionLeaseResponse>> HeartbeatWorldSessionAsync(
        Guid worldSessionId,
        string worldId,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        return PostAsync<WorldSessionCredentialRequest, WorldSessionLeaseResponse>(
            $"/api/world-sessions/{worldSessionId}/heartbeat",
            new WorldSessionCredentialRequest(worldId, sessionToken),
            IsValidWorldSessionLease,
            cancellationToken);
    }

    public Task<AuthServiceResult<WorldSessionLeaseResponse>> ReleaseWorldSessionAsync(
        Guid worldSessionId,
        string worldId,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        return PostAsync<WorldSessionCredentialRequest, WorldSessionLeaseResponse>(
            $"/api/world-sessions/{worldSessionId}/release",
            new WorldSessionCredentialRequest(worldId, sessionToken),
            IsValidWorldSessionLease,
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
            using var response = await httpClient.PostAsJsonAsync(path, requestBody, cancellationToken);

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

            if (string.Equals(problem.Code, "invalid_service_credentials", StringComparison.Ordinal))
            {
                return AuthServiceResult<TResponse>.Failure(
                    StatusCodes.Status502BadGateway,
                    "auth_service_authentication_failed",
                    "WorldServer could not authenticate with AuthService.");
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
                StatusCodes.Status504GatewayTimeout,
                "auth_service_timeout",
                "AuthService did not respond before the timeout.");
        }
        catch (HttpRequestException)
        {
            return AuthServiceResult<TResponse>.Failure(
                StatusCodes.Status503ServiceUnavailable,
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
            StatusCodes.Status502BadGateway,
            "invalid_auth_response",
            "AuthService returned an invalid response.");
    }

    private static bool IsValidConsumedTicket(ConsumedJoinTicketResponse response)
    {
        return response.AccountId != Guid.Empty
            && response.CharacterId != Guid.Empty
            && response.WorldSessionId != Guid.Empty
            && !string.IsNullOrWhiteSpace(response.CharacterName)
            && !string.IsNullOrWhiteSpace(response.WorldId)
            && !string.IsNullOrWhiteSpace(response.WorldSessionToken)
            && response.SessionExpiresAt != default;
    }

    private static bool IsValidWorldSessionLease(WorldSessionLeaseResponse response)
    {
        return response.WorldSessionId != Guid.Empty
            && response.CharacterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(response.WorldId)
            && response.ExpiresAt != default;
    }
}
