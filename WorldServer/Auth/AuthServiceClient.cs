using System.Net.Http.Json;
using ShooterMmo.Shared.Health;

namespace WorldServer.Auth;

public sealed class AuthServiceClient(HttpClient httpClient)
{
    public async Task<DependencyHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/health", cancellationToken);
            return new DependencyHealth(
                "auth-service",
                new Uri(httpClient.BaseAddress!, "/health").ToString(),
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (HttpRequestException exception)
        {
            return new DependencyHealth("auth-service", httpClient.BaseAddress!.ToString(), false, exception.Message);
        }
        catch (TaskCanceledException exception)
        {
            return new DependencyHealth("auth-service", httpClient.BaseAddress!.ToString(), false, exception.Message);
        }
    }

    public Task<AuthServiceResult<ConsumedJoinTicketResponse>> ConsumeJoinTicketAsync(
        string ticket,
        string worldId,
        CancellationToken cancellationToken)
    {
        return PostAsync<ConsumeJoinTicketRequest, ConsumedJoinTicketResponse>(
            "/api/world-join-tickets/consume",
            new ConsumeJoinTicketRequest(ticket, worldId),
            "AuthService returned an empty join ticket response.",
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
            "AuthService returned an empty world session heartbeat response.",
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
            "AuthService returned an empty world session release response.",
            cancellationToken);
    }

    private async Task<AuthServiceResult<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest requestBody,
        string emptyResponseMessage,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, requestBody, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadFromJsonAsync<TResponse>(
                cancellationToken: cancellationToken);

            return responseBody is null
                ? AuthServiceResult<TResponse>.Failure(
                    StatusCodes.Status502BadGateway,
                    "invalid_auth_response",
                    emptyResponseMessage)
                : AuthServiceResult<TResponse>.Success(responseBody);
        }

        var error = await response.Content.ReadFromJsonAsync<AuthServiceErrorResponse>(
            cancellationToken: cancellationToken);

        return AuthServiceResult<TResponse>.Failure(
            (int)response.StatusCode,
            error?.Code ?? "auth_service_error",
            error?.Message ?? "AuthService rejected the request.");
    }
}
