using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed class FullStackStressClient(
    HttpClient httpClient,
    FullStackStressMetrics metrics)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        await SendNoContentAsync(
            "health_ready",
            HttpMethod.Get,
            "/health/ready",
            null,
            null,
            cancellationToken);
    }

    public Task<FullStackAuthResponse> RegisterAsync(
        string email,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackAuthResponse>(
            "account_register",
            HttpMethod.Post,
            "/api/accounts/register",
            new FullStackRegisterRequest(email, username, password),
            null,
            cancellationToken);
    }

    public Task<FullStackAuthResponse> LoginAsync(
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackAuthResponse>(
            "account_login",
            HttpMethod.Post,
            "/api/accounts/login",
            new FullStackLoginRequest(login, password),
            null,
            cancellationToken);
    }

    public Task<FullStackCharacterResponse> CreateCharacterAsync(
        string sessionToken,
        string name,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackCharacterResponse>(
            "character_create",
            HttpMethod.Post,
            "/api/characters",
            new FullStackCreateCharacterRequest(name),
            sessionToken,
            cancellationToken);
    }

    public Task<IReadOnlyList<FullStackCharacterResponse>> ListCharactersAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        return SendAsync<IReadOnlyList<FullStackCharacterResponse>>(
            "character_list",
            HttpMethod.Get,
            "/api/characters",
            null,
            sessionToken,
            cancellationToken);
    }

    public Task<IReadOnlyList<FullStackShardResponse>> ListShardsAsync(
        CancellationToken cancellationToken)
    {
        return SendAsync<IReadOnlyList<FullStackShardResponse>>(
            "shard_list",
            HttpMethod.Get,
            "/api/shards",
            null,
            null,
            cancellationToken);
    }

    public Task<FullStackJoinShardResponse> JoinShardAsync(
        string sessionToken,
        string shardId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackJoinShardResponse>(
            "join_ticket",
            HttpMethod.Post,
            $"/api/shards/{Uri.EscapeDataString(shardId)}/join",
            new FullStackJoinShardRequest(characterId),
            sessionToken,
            cancellationToken);
    }

    public Task<FullStackCharacterInventoryResponse> GetInventoryAsync(
        string sessionToken,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackCharacterInventoryResponse>(
            "inventory_refresh",
            HttpMethod.Get,
            $"/api/characters/{characterId:D}/item-state",
            null,
            sessionToken,
            cancellationToken);
    }

    public Task<FullStackStackStressInventoryFixtureResponse> SeedInventoryFixtureAsync(
        string sessionToken,
        Guid characterId,
        string workload,
        string runId,
        string fixtureSecret,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackStackStressInventoryFixtureResponse>(
            "stress_inventory_fixture",
            HttpMethod.Post,
            $"/api/development/stack-stress/characters/{characterId:D}/inventory-fixture",
            new FullStackStackStressInventoryFixtureRequest(workload, runId),
            sessionToken,
            cancellationToken,
            fixtureSecret);
    }

    public Task<FullStackStackStressLootHotspotResponse> CreateLootHotspotAsync(
        string sessionToken,
        FullStackStackStressLootHotspotRequest request,
        string fixtureSecret,
        CancellationToken cancellationToken)
    {
        return SendAsync<FullStackStackStressLootHotspotResponse>(
            "stress_loot_hotspot_fixture",
            HttpMethod.Post,
            "/api/development/stack-stress/loot-hotspot",
            request,
            sessionToken,
            cancellationToken,
            fixtureSecret);
    }

    public Task LogoutAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        return SendNoContentAsync(
            "account_logout",
            HttpMethod.Post,
            "/api/accounts/logout",
            null,
            sessionToken,
            cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        string operation,
        HttpMethod method,
        string path,
        object? body,
        string? sessionToken,
        CancellationToken cancellationToken,
        string? fixtureSecret = null)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = CreateRequest(
                method,
                path,
                body,
                sessionToken,
                fixtureSecret);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var problem = await TryReadProblemAsync(response, cancellationToken);
                var code = problem?.Code ?? $"http_{statusCode}";
                var message = problem?.Detail
                    ?? $"AuthService returned HTTP {statusCode} {response.ReasonPhrase}.";
                metrics.Record(operation, ElapsedMilliseconds(started), statusCode, code);
                throw new FullStackApiException(code, message, statusCode);
            }

            T? value;
            try
            {
                value = await response.Content.ReadFromJsonAsync<T>(
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException exception)
            {
                metrics.Record(
                    operation,
                    ElapsedMilliseconds(started),
                    statusCode,
                    "invalid_response");
                throw new FullStackApiException(
                    "invalid_response",
                    $"AuthService returned invalid JSON: {exception.Message}",
                    statusCode);
            }

            if (value is null)
            {
                metrics.Record(
                    operation,
                    ElapsedMilliseconds(started),
                    statusCode,
                    "invalid_response");
                throw new FullStackApiException(
                    "invalid_response",
                    "AuthService returned an empty JSON response.",
                    statusCode);
            }

            metrics.Record(operation, ElapsedMilliseconds(started), statusCode, null);
            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            metrics.Record(operation, ElapsedMilliseconds(started), 0, "http_timeout");
            throw new FullStackApiException(
                "http_timeout",
                $"AuthService did not complete {operation} before the HTTP timeout.",
                0);
        }
        catch (HttpRequestException exception)
        {
            metrics.Record(operation, ElapsedMilliseconds(started), 0, "http_unavailable");
            throw new FullStackApiException("http_unavailable", exception.Message, 0);
        }
    }

    private async Task SendNoContentAsync(
        string operation,
        HttpMethod method,
        string path,
        object? body,
        string? sessionToken,
        CancellationToken cancellationToken,
        string? fixtureSecret = null)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = CreateRequest(
                method,
                path,
                body,
                sessionToken,
                fixtureSecret);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var problem = await TryReadProblemAsync(response, cancellationToken);
                var code = problem?.Code ?? $"http_{statusCode}";
                var message = problem?.Detail
                    ?? $"AuthService returned HTTP {statusCode} {response.ReasonPhrase}.";
                metrics.Record(operation, ElapsedMilliseconds(started), statusCode, code);
                throw new FullStackApiException(code, message, statusCode);
            }

            metrics.Record(operation, ElapsedMilliseconds(started), statusCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            metrics.Record(operation, ElapsedMilliseconds(started), 0, "http_timeout");
            throw new FullStackApiException(
                "http_timeout",
                $"AuthService did not complete {operation} before the HTTP timeout.",
                0);
        }
        catch (HttpRequestException exception)
        {
            metrics.Record(operation, ElapsedMilliseconds(started), 0, "http_unavailable");
            throw new FullStackApiException("http_unavailable", exception.Message, 0);
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        object? body,
        string? sessionToken,
        string? fixtureSecret)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                sessionToken);
        }

        if (!string.IsNullOrWhiteSpace(fixtureSecret))
        {
            request.Headers.Add("X-Stack-Stress-Fixture-Key", fixtureSecret);
        }

        return request;
    }

    private static async Task<FullStackProblem?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<FullStackProblem>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double ElapsedMilliseconds(long started)
    {
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}

public sealed class FullStackApiException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

public sealed record FullStackRegisterRequest(string Email, string Username, string Password);

public sealed record FullStackLoginRequest(string Login, string Password);

public sealed record FullStackAuthResponse(
    Guid AccountId,
    string Username,
    Guid SessionId,
    string SessionToken,
    DateTime ExpiresAt);

public sealed record FullStackCreateCharacterRequest(string Name);

public sealed record FullStackCharacterResponse(
    Guid Id,
    string Name,
    long Currency,
    DateTime CreatedAt);

public sealed record FullStackShardResponse(
    string Id,
    string DisplayName,
    string WorldId,
    string FleetId,
    string FleetDisplayName,
    string RegionCode,
    string RuleSet,
    bool IsOnline,
    int ActivePlayers,
    int Capacity);

public sealed record FullStackJoinShardRequest(Guid CharacterId);

public sealed record FullStackSimulationEndpointResponse(
    string WorkerId,
    string RuntimeId,
    string Host,
    int UdpPort,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision);

public sealed record FullStackJoinShardResponse(
    FullStackShardResponse Shard,
    FullStackSimulationEndpointResponse Endpoint,
    Guid CharacterId,
    string JoinTicket,
    DateTime ExpiresAt,
    bool IsReconnect);

public sealed record FullStackProblem(string? Code, string? Detail);

public sealed record FullStackItemInstanceResponse(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision);

public sealed record FullStackItemSlotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags,
    FullStackItemInstanceResponse? Item);

public sealed record FullStackItemContainerResponse(
    Guid ContainerId,
    string ContainerType,
    long Revision,
    int? SlotCapacity,
    IReadOnlyList<FullStackItemSlotResponse> Slots);

public sealed record FullStackEquipmentSlotResponse(
    string EquipmentSlotId,
    int SortOrder,
    FullStackItemInstanceResponse? Item);

public sealed record FullStackEquippedBagResponse(
    FullStackItemInstanceResponse Item,
    FullStackItemContainerResponse Contents);

public sealed record FullStackSecureContainerResponse(
    string TierId,
    long EntitlementRevision,
    FullStackItemContainerResponse Contents);

public sealed record FullStackRecoveryStorageResponse(
    Guid ContainerId,
    long Revision,
    IReadOnlyList<object> Deliveries);

public sealed record FullStackCharacterInventoryResponse(
    Guid CharacterId,
    string CatalogRevision,
    long ItemStateRevision,
    FullStackItemContainerResponse PermanentInventory,
    IReadOnlyList<FullStackEquipmentSlotResponse> Equipment,
    FullStackEquippedBagResponse? EquippedBag,
    FullStackItemContainerResponse Bank,
    FullStackSecureContainerResponse SecureContainer,
    FullStackRecoveryStorageResponse RecoveryStorage,
    long CarriedWeight,
    long CarryCapacity,
    int LoadRatioBasisPoints,
    bool SprintEligible,
    int MovementMultiplierBasisPoints);

public sealed record FullStackStackStressInventoryFixtureRequest(
    string Workload,
    string RunId);

public sealed record FullStackStackStressInventoryFixtureResponse(
    string Workload,
    FullStackCharacterInventoryResponse Inventory);

public sealed record FullStackStackStressLootHotspotRequest(
    string RunId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double LifetimeSeconds);

public sealed record FullStackStackStressLootHotspotResponse(
    Guid CorpseId,
    int LootStacks,
    int QuantityPerStack,
    DateTime ExpiresAt);
