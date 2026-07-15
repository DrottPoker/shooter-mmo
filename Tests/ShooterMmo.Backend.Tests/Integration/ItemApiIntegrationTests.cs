using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Items;
using AuthService.Simulation;
using Dapper;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class ItemApiIntegrationTests
{
    private const string LocalWorkerId = "local-simulation-worker-1";
    private const string LocalRuntimeId = "integration-worker-runtime";
    private const string LocalShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task CatalogUsesRevisionEtagAndExcludesClientPresentationAssets()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var host = await ItemApiTestHost.StartAsync(context);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            player.Registration.SessionToken);

        using var response = await host.Client.GetAsync("/api/item-catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("public", response.Headers.CacheControl?.Public == true ? "public" : null);
        Assert.True(response.Headers.CacheControl?.MustRevalidate);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("revision", out var revision));
        Assert.Equal($"\"{revision.GetString()}\"", response.Headers.ETag!.Tag);
        Assert.DoesNotContain("icon", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assetReference", json, StringComparison.OrdinalIgnoreCase);

        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/item-catalog");
        unchangedRequest.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var unchanged = await host.Client.SendAsync(unchangedRequest);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(response.Headers.ETag, unchanged.Headers.ETag);
        Assert.Equal(string.Empty, await unchanged.Content.ReadAsStringAsync());
    }

    [PostgresIntegrationFact]
    public async Task CharacterReadsAreAuthenticatedOwnerScopedAndNeverCacheable()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var owner = await context.RegisterPlayerAsync();
        var other = await context.RegisterPlayerAsync(
            "other-api@example.com",
            "other_api_player",
            "Other API Hero");
        await using var host = await ItemApiTestHost.StartAsync(context);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            owner.Registration.SessionToken);

        foreach (var path in new[]
                 {
                     $"/api/characters/{owner.Character.Id}/item-state",
                     $"/api/characters/{owner.Character.Id}/bank",
                     $"/api/characters/{owner.Character.Id}/recovery"
                 })
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
        }

        using (var itemStateResponse = await host.Client.GetAsync(
                   $"/api/characters/{owner.Character.Id}/item-state"))
        {
            var itemState = await itemStateResponse.Content
                .ReadFromJsonAsync<CharacterInventorySnapshotResponse>();
            Assert.NotNull(itemState);
            Assert.Equal("secure_container.base", itemState.SecureContainer.TierId);
            using var tierChange = await host.Client.PostAsJsonAsync(
                "/api/items/secure-container-tier",
                new ChangeAccountSecureContainerTierRequest(
                    Guid.NewGuid(),
                    itemState.SecureContainer.EntitlementRevision,
                    itemState.SecureContainer.TierId,
                    [new CharacterRevisionExpectation(owner.Character.Id, itemState.ItemStateRevision)]));
            Assert.Equal(HttpStatusCode.OK, tierChange.StatusCode);
            Assert.True(tierChange.Headers.CacheControl?.NoStore);
        }

        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            other.Registration.SessionToken);
        using var forbiddenRead = await host.Client.GetAsync(
            $"/api/characters/{owner.Character.Id}/bank");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenRead.StatusCode);
        Assert.Equal("character_bank_not_found", await ReadProblemCodeAsync(forbiddenRead));

        host.Client.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await host.Client.GetAsync(
            $"/api/characters/{owner.Character.Id}/item-state");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("invalid_session_token", await ReadProblemCodeAsync(unauthenticated));
    }

    [PostgresIntegrationFact]
    public async Task OfflineMutationSucceedsAndActiveSimulationSessionReturnsStableConflict()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var grant = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "medical.field_dressing",
                    1,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(grant.Succeeded, grant.Error?.Message);
        var item = Assert.Single(grant.ItemRevisions);
        var character = Assert.Single(grant.CharacterRevisions);

        await using var host = await ItemApiTestHost.StartAsync(context);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            player.Registration.SessionToken);
        using var moved = await host.Client.PostAsJsonAsync(
            $"/api/characters/{player.Character.Id}/item-operations/relocate",
            new RelocateAccountItemRequest(
                Guid.NewGuid(),
                character.Revision,
                item.ItemInstanceId,
                item.Revision,
                state.BankContainerId,
                0));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.True(moved.Headers.CacheControl?.NoStore);
        var moveResult = await moved.Content.ReadFromJsonAsync<ItemTransactionResult>();
        Assert.NotNull(moveResult);
        item = Assert.Single(moveResult.ItemRevisions);
        character = Assert.Single(moveResult.CharacterRevisions);

        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                LocalWorkerId,
                LocalRuntimeId,
                LocalShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);

        using var blocked = await host.Client.PostAsJsonAsync(
            $"/api/characters/{player.Character.Id}/item-operations/relocate",
            new RelocateAccountItemRequest(
                Guid.NewGuid(),
                character.Revision,
                item.ItemInstanceId,
                item.Revision,
                state.PermanentInventoryContainerId,
                0));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.OfflineAccessRequired,
            await ReadProblemCodeAsync(blocked));
    }

    [PostgresIntegrationFact]
    public async Task StackAndDestroyRoutesReturnStableValidationProblems()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using var host = await ItemApiTestHost.StartAsync(context);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            player.Registration.SessionToken);

        var requests = new (string Path, object Body)[]
        {
            (
                $"/api/characters/{player.Character.Id}/item-operations/split",
                new SplitAccountItemStackRequest(
                    Guid.Empty,
                    0,
                    Guid.NewGuid(),
                    0,
                    1,
                    Guid.NewGuid(),
                    null)),
            (
                $"/api/characters/{player.Character.Id}/item-operations/merge",
                new MergeAccountItemStacksRequest(
                    Guid.Empty,
                    0,
                    Guid.NewGuid(),
                    0,
                    Guid.NewGuid(),
                    0)),
            (
                $"/api/characters/{player.Character.Id}/item-operations/destroy",
                new DestroyAccountItemRequest(
                    Guid.Empty,
                    0,
                    Guid.NewGuid(),
                    0))
        };
        foreach (var request in requests)
        {
            using var response = await host.Client.PostAsJsonAsync(request.Path, request.Body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(ItemApiErrorCodes.OperationIdRequired, await ReadProblemCodeAsync(response));
        }
    }

    [PostgresIntegrationFact]
    public async Task RecoveryApiRejectsDepositsAndClaimsSystemDeliveriesToBank()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var grant = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "medical.field_dressing",
                    1,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(grant.Succeeded, grant.Error?.Message);
        var item = Assert.Single(grant.ItemRevisions);
        var character = Assert.Single(grant.CharacterRevisions);

        await using var host = await ItemApiTestHost.StartAsync(context);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            player.Registration.SessionToken);
        using var deposit = await host.Client.PostAsJsonAsync(
            $"/api/characters/{player.Character.Id}/item-operations/relocate",
            new RelocateAccountItemRequest(
                Guid.NewGuid(),
                character.Revision,
                item.ItemInstanceId,
                item.Revision,
                state.RecoveryStorageContainerId,
                null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deposit.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.RecoveryAccessRequired,
            await ReadProblemCodeAsync(deposit));

        var delivery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    player.Character.Id,
                    character.Revision,
                    "phase6_test",
                    Guid.NewGuid().ToString("N"),
                    DateTime.UtcNow,
                    null,
                    [new ItemRevisionExpectation(item.ItemInstanceId, item.Revision)])),
            CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Error?.Message);
        var deliveryId = Assert.Single(delivery.RecoveryDeliveryIds);
        item = Assert.Single(delivery.ItemRevisions);
        character = Assert.Single(delivery.CharacterRevisions);

        using var claim = await host.Client.PostAsJsonAsync(
            $"/api/characters/{player.Character.Id}/recovery/{deliveryId}/claim",
            new ClaimAccountRecoveryDeliveryRequest(
                Guid.NewGuid(),
                character.Revision,
                0,
                state.BankContainerId,
                [new ItemRevisionExpectation(item.ItemInstanceId, item.Revision)]));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var claimResult = await claim.Content.ReadFromJsonAsync<ItemTransactionResult>();
        Assert.NotNull(claimResult);
        Assert.True(claimResult.Succeeded);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            state.BankContainerId,
            await connection.QuerySingleAsync<Guid>(
                "select container_id from item_instances where id = @ItemInstanceId;",
                new { item.ItemInstanceId }));
    }

    [PostgresIntegrationFact]
    public async Task RecoveryClaimRollsBackWhenDestinationWouldExceedHardCap()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var heavy = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "material.iron_ore",
                    46,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(heavy.Succeeded, heavy.Error?.Message);
        var character = Assert.Single(heavy.CharacterRevisions);
        Assert.Equal(276, character.CarriedWeight);

        var recoverable = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new GrantItemCommand(
                    player.Character.Id,
                    character.Revision,
                    "medical.field_dressing",
                    3,
                    state.BankContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(recoverable.Succeeded, recoverable.Error?.Message);
        var item = Assert.Single(recoverable.ItemRevisions);
        character = Assert.Single(recoverable.CharacterRevisions);
        Assert.Equal(276, character.CarriedWeight);

        var delivery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    player.Character.Id,
                    character.Revision,
                    "phase6_hard_cap",
                    Guid.NewGuid().ToString("N"),
                    DateTime.UtcNow,
                    null,
                    [new ItemRevisionExpectation(item.ItemInstanceId, item.Revision)])),
            CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Error?.Message);
        var deliveryId = Assert.Single(delivery.RecoveryDeliveryIds);
        item = Assert.Single(delivery.ItemRevisions);
        character = Assert.Single(delivery.CharacterRevisions);

        await using var host = await ItemApiTestHost.StartAsync(context);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            player.Registration.SessionToken);
        using var claim = await host.Client.PostAsJsonAsync(
            $"/api/characters/{player.Character.Id}/recovery/{deliveryId}/claim",
            new ClaimAccountRecoveryDeliveryRequest(
                Guid.NewGuid(),
                character.Revision,
                0,
                state.PermanentInventoryContainerId,
                [new ItemRevisionExpectation(item.ItemInstanceId, item.Revision)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, claim.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.CarryWeightLimitExceeded,
            await ReadProblemCodeAsync(claim));

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            state.RecoveryStorageContainerId,
            await connection.QuerySingleAsync<Guid>(
                "select container_id from item_instances where id = @ItemInstanceId;",
                new { item.ItemInstanceId }));
        Assert.Null(await connection.QuerySingleAsync<DateTime?>(
            "select claimed_at from recovery_deliveries where id = @DeliveryId;",
            new { DeliveryId = deliveryId }));
    }

    private static async Task<string> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()
            ?? throw new InvalidOperationException("Problem Details has no code.");
    }

    private static async Task<CharacterStateRow> LoadStateAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<CharacterStateRow>(
            """
            select
                revision as "Revision",
                permanent_inventory_container_id as "PermanentInventoryContainerId",
                bank_container_id as "BankContainerId",
                recovery_storage_container_id as "RecoveryStorageContainerId"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId });
    }

    private sealed class CharacterStateRow
    {
        public long Revision { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        public Guid RecoveryStorageContainerId { get; set; }
    }
}
