using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mmorpg.Tools.DeploySmokeTest;

// What GameService answered: the status, the body, and the code of a Problem Details answer.
public sealed record StackResponse(
    HttpStatusCode Status,
    string Body,
    string? ProblemCode,
    TimeSpan? RetryAfter)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public T Read<T>()
    {
        return JsonSerializer.Deserialize<T>(Body, StackApi.Json)
            ?? throw new SmokeException($"GameService returned an empty {typeof(T).Name}.");
    }
}

public sealed class SmokeException(string message) : Exception(message);

// The HTTP surface of GameService as a player's client calls it, through the proxy. Each typed call
// fails with a SmokeException that names the status and the problem code, never the request.
public sealed class StackApi(HttpClient client, TimeSpan? rateLimitMargin = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<StackResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        return SendCoreAsync(method, path, body, bearerToken, headers, cancellationToken);
    }

    public async Task<AuthAnswer> RegisterAsync(string email, string username, string password, CancellationToken cancellationToken = default)
    {
        var response = await SendPacedAsync("/api/accounts/register", new { email, username, password }, cancellationToken);
        return Succeeded(response, "register").Read<AuthAnswer>();
    }

    public async Task<AuthAnswer> LoginAsync(string login, string password, CancellationToken cancellationToken = default)
    {
        var response = await SendPacedAsync("/api/accounts/login", new { login, password }, cancellationToken);
        return Succeeded(response, "login").Read<AuthAnswer>();
    }

    public Task<StackResponse> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        return SendAsync(HttpMethod.Get, "/api/accounts/session", bearerToken: sessionToken, cancellationToken: cancellationToken);
    }

    public async Task<CharacterAnswer> CreateCharacterAsync(string sessionToken, string name, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "/api/characters",
            new { name },
            sessionToken,
            cancellationToken: cancellationToken);
        return Succeeded(response, "character create").Read<CharacterAnswer>();
    }

    public async Task<IReadOnlyList<CharacterAnswer>> ListCharactersAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/characters", bearerToken: sessionToken, cancellationToken: cancellationToken);
        return Succeeded(response, "character list").Read<CharacterAnswer[]>();
    }

    public async Task<IReadOnlyList<ShardAnswer>> ListShardsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/shards", cancellationToken: cancellationToken);
        return Succeeded(response, "shard list").Read<ShardAnswer[]>();
    }

    public async Task<JoinAnswer> JoinAsync(string sessionToken, string shardId, Guid characterId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            $"/api/shards/{Uri.EscapeDataString(shardId)}/join",
            new { characterId },
            sessionToken,
            cancellationToken: cancellationToken);
        return Succeeded(response, "join").Read<JoinAnswer>();
    }

    public Task<StackResponse> LogoutAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        return SendAsync(HttpMethod.Post, "/api/accounts/logout", bearerToken: sessionToken, cancellationToken: cancellationToken);
    }

    // Registering and logging in are limited per client address, and a second run within the same
    // minute meets the window of the first. It waits out a Retry-After of at most a minute, twice,
    // as a player's client waits before it tries again, and fails with the refusal after that. The\n    // margin is how long it waits beyond the Retry-After.
    private async Task<StackResponse> SendPacedAsync(string path, object body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken: cancellationToken);
            if (response.Status != HttpStatusCode.TooManyRequests
                || attempt >= 2
                || response.RetryAfter is not { } wait
                || wait > TimeSpan.FromSeconds(65))
            {
                return response;
            }

            await Task.Delay(wait + (rateLimitMargin ?? TimeSpan.FromSeconds(1)), cancellationToken);
        }
    }

    private async Task<StackResponse> SendCoreAsync(
        HttpMethod method,
        string path,
        object? body,
        string? bearerToken,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new StackResponse(response.StatusCode, text, ReadProblemCode(text), response.Headers.RetryAfter?.Delta);
    }

    private static StackResponse Succeeded(StackResponse response, string operation)
    {
        if (!response.IsSuccess)
        {
            throw new SmokeException(
                $"{operation} answered {(int)response.Status}{(response.ProblemCode is null ? string.Empty : $" {response.ProblemCode}")}.");
        }

        return response;
    }

    private static string? ReadProblemCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AuthAnswer(Guid AccountId, string Username, Guid SessionId, string SessionToken, DateTime ExpiresAt);

public sealed record CharacterAnswer(Guid Id, string Name);

public sealed record ShardAnswer(string Id, string DisplayName, string WorldId, bool IsOnline, int ActivePlayers, int Capacity);

public sealed record EndpointAnswer(
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int ProtocolVersion);
