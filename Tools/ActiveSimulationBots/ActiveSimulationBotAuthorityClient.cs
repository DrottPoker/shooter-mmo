using System.Net.Http.Json;
using System.Text.Json;

namespace ShooterMmo.Tools.ActiveSimulationBots;

public sealed class ActiveSimulationBotAuthorityClient(
    HttpClient httpClient,
    ActiveSimulationBotOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ActiveSimulationBotTicketResult> IssueTicketAsync(
        Guid botInstanceId,
        int botIndex,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/development/simulation-bots/join-tickets")
        {
            Content = JsonContent.Create(
                new ActiveSimulationBotTicketRequest(
                    botInstanceId,
                    botIndex,
                    options.ShardId),
                options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(
            AuthServiceHeaderNames.ActiveSimulationBotsKey,
            options.AuthoritySecret);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                ActiveSimulationBotTicket? ticket;
                try
                {
                    ticket = await response.Content.ReadFromJsonAsync<ActiveSimulationBotTicket>(
                        JsonOptions,
                        cancellationToken);
                }
                catch (JsonException)
                {
                    ticket = null;
                }

                return ticket is null
                    ? ActiveSimulationBotTicketResult.Failure(
                        (int)response.StatusCode,
                        "invalid_authority_response",
                        "AuthService returned an empty development bot ticket response.")
                    : ActiveSimulationBotTicketResult.Success(ticket);
            }

            var problem = await TryReadProblemAsync(response, cancellationToken);
            return ActiveSimulationBotTicketResult.Failure(
                (int)response.StatusCode,
                problem?.Code ?? "development_bot_ticket_failed",
                problem?.Detail
                    ?? $"AuthService returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ActiveSimulationBotTicketResult.Failure(
                0,
                "authority_timeout",
                "AuthService did not issue a development bot ticket before the HTTP timeout.");
        }
        catch (HttpRequestException exception)
        {
            return ActiveSimulationBotTicketResult.Failure(
                0,
                "authority_unavailable",
                exception.Message);
        }
    }

    private static async Task<ActiveSimulationBotProblem?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ActiveSimulationBotProblem>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class AuthServiceHeaderNames
{
    public const string ActiveSimulationBotsKey = "X-Active-Simulation-Bots-Key";
}

public sealed record ActiveSimulationBotTicketRequest(
    Guid BotInstanceId,
    int BotIndex,
    string ShardId);

public sealed record ActiveSimulationBotTicket(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    ActiveSimulationBotEndpoint Endpoint,
    string JoinTicket,
    DateTime ExpiresAt);

public sealed record ActiveSimulationBotEndpoint(
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record ActiveSimulationBotProblem(
    string? Code,
    string? Detail);

public sealed record ActiveSimulationBotTicketResult(
    ActiveSimulationBotTicket? Ticket,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Ticket is not null;

    public static ActiveSimulationBotTicketResult Success(ActiveSimulationBotTicket ticket)
    {
        return new ActiveSimulationBotTicketResult(ticket, 200, null, null);
    }

    public static ActiveSimulationBotTicketResult Failure(
        int statusCode,
        string errorCode,
        string errorMessage)
    {
        return new ActiveSimulationBotTicketResult(
            null,
            statusCode,
            errorCode,
            errorMessage);
    }
}
