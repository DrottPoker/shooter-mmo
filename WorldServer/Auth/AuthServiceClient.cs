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

    public async Task<AuthServiceResult<ConsumedJoinTicketResponse>> ConsumeJoinTicketAsync(
        string ticket,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/world-join-tickets/consume",
            new ConsumeJoinTicketRequest(ticket),
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var consumedTicket = await response.Content.ReadFromJsonAsync<ConsumedJoinTicketResponse>(
                cancellationToken: cancellationToken);

            return consumedTicket is null
                ? AuthServiceResult<ConsumedJoinTicketResponse>.Failure(
                    StatusCodes.Status502BadGateway,
                    "invalid_auth_response",
                    "AuthService returned an empty join ticket response.")
                : AuthServiceResult<ConsumedJoinTicketResponse>.Success(consumedTicket);
        }

        var error = await response.Content.ReadFromJsonAsync<AuthServiceErrorResponse>(
            cancellationToken: cancellationToken);

        return AuthServiceResult<ConsumedJoinTicketResponse>.Failure(
            (int)response.StatusCode,
            error?.Code ?? "auth_service_error",
            error?.Message ?? "AuthService rejected the join ticket.");
    }
}

