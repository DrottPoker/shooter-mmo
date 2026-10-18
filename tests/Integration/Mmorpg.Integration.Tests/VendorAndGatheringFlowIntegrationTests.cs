using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.Testing;
using Mmorpg.WorldData.Actors;
using ServerItemOperationRequest = Mmorpg.GameService.Items.SimulationItemOperationRequest;
using ServerJoinTicket = Mmorpg.GameService.Placement.ConsumedSimulationJoinTicketResponse;
using WorkerAccessRequest = Mmorpg.SimulationWorker.GameServiceApi.SimulationItemAccessRequest;
using WorkerItemOperationRequest = Mmorpg.SimulationWorker.GameServiceApi.SimulationItemOperationRequest;
using WorkerJoinTicket = Mmorpg.SimulationWorker.GameServiceApi.ConsumedSimulationJoinTicketResponse;

namespace Mmorpg.Integration.Tests;

// The worker's own gathering request and response validation against the GameService item
// operations, through the route's JSON contract and HTTP result mapping: buy a pickaxe, mine
// ore with it, sell the ore, and buy a potion with the proceeds.
public sealed class VendorAndGatheringFlowIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string VendorId = "vendor.fixture_store";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [PostgresIntegrationFact]
    public async Task MinedOreSoldToTheVendorPaysForAPotion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "vendor-flow@example.com",
            "vendor_flow",
            "Vendor Flow");
        await CreditAsync(context, player.Character.Id, 100);
        var session = await JoinAsync(context, player);
        using var httpClient = new HttpClient(new InProcessItemOperations(context))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var client = new GameServiceClient(httpClient);
        var revision = session.CarryState.ItemStateRevision;

        revision = await CommitAsync(client, session, Request(session, ItemOperationKinds.VendorPurchase, revision) with
        {
            DefinitionId = "fixture.pickaxe",
            Quantity = 1,
            ExpectedPrice = 50
        });
        var pickaxe = await FindAsync(context, player, "fixture.pickaxe");
        revision = await CommitAsync(client, session, Request(session, ItemOperationKinds.Equip, revision, null) with
        {
            ItemInstanceId = pickaxe.ItemInstanceId,
            ExpectedItemRevision = pickaxe.Revision,
            EquipmentSlotId = "tool"
        });

        var gathering = new SimulationGatheringService(client);
        var profile = new WorldActorResourceProfileDefinition
        {
            Id = "resource.fixture",
            RequiredToolKind = "pickaxe",
            YieldDefinitionId = "fixture.ore",
            YieldQuantity = 1,
            CycleSeconds = 3f,
            Charges = 5,
            RespawnSeconds = 30f
        };
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var mined = await gathering.GatherAsync(
                session,
                new SimulationGatheringRequest(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "resource.mine",
                    "fixture.node:0000",
                    profile),
                cancellationToken);
            Assert.True(mined.Succeeded, mined.Error?.Code);
            revision = Assert.Single(mined.Transaction!.CharacterRevisions).Revision;
        }

        var ore = await FindAsync(context, player, "fixture.ore");
        Assert.Equal(3, ore.Quantity);
        revision = await CommitAsync(client, session, Request(session, ItemOperationKinds.VendorSale, revision) with
        {
            ItemInstanceId = ore.ItemInstanceId,
            ExpectedItemRevision = ore.Revision,
            Quantity = 3,
            ExpectedPrice = 12
        });
        revision = await CommitAsync(client, session, Request(session, ItemOperationKinds.VendorPurchase, revision) with
        {
            DefinitionId = "fixture.potion",
            Quantity = 1,
            ExpectedPrice = 25
        });

        var snapshot = await SnapshotAsync(context, player);
        Assert.Equal(revision, snapshot.ItemStateRevision);
        Assert.Equal(100 - 50 + 12 - 25, snapshot.Currency);
        Assert.DoesNotContain(Items(snapshot), item => item.DefinitionId == "fixture.ore");
        Assert.Contains(Items(snapshot), item => item.DefinitionId == "fixture.potion");
        Assert.Equal(
            "fixture.pickaxe",
            snapshot.Equipment.Single(slot => slot.EquipmentSlotId == "tool").Item?.DefinitionId);
        await using var connection = await context.DataSource.OpenConnectionAsync(cancellationToken);
        var ledger = (await connection.QueryAsync<(string SourceKind, long Delta)>(
            """
            select source_kind as "SourceKind", delta as "Delta"
            from character_currency_ledger
            where character_id = @CharacterId
            order by id;
            """,
            new { CharacterId = player.Character.Id })).ToArray();
        Assert.Equal(
            ["development", "vendor_purchase", "vendor_sale", "vendor_purchase"],
            ledger.Select(entry => entry.SourceKind));
        Assert.Equal(snapshot.Currency, ledger.Sum(entry => entry.Delta));
    }

    private static async Task<long> CommitAsync(
        GameServiceClient client,
        ActiveSimulationSession session,
        WorkerItemOperationRequest request)
    {
        var result = await client.MutateSimulationItemsAsync(
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Code + ": " + result.Error?.Message);
        return Assert.Single(result.Value!.CharacterRevisions).Revision;
    }

    private static WorkerItemOperationRequest Request(
        ActiveSimulationSession session,
        string operationKind,
        long expectedCharacterRevision,
        string? vendorProfileId = VendorId)
    {
        return new WorkerItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new WorkerAccessRequest(false, false, false, false, vendorProfileId),
            operationKind,
            expectedCharacterRevision,
            InteractionSessionId: Guid.NewGuid(),
            CapabilityId: vendorProfileId is null ? null : "services.vendor");
    }

    private static async Task CreditAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        long amount)
    {
        var wallet = context.Services.GetRequiredService<ICharacterWallet>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var credit = await wallet.CreditAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            amount,
            new CurrencyChangeSource(CurrencySourceKinds.Development, "vendor_flow_test", null),
            CancellationToken.None);
        Assert.True(credit.Succeeded);
        await transaction.CommitAsync();
    }

    // The worker session comes from the join response as the worker reads it.
    private static async Task<ActiveSimulationSession> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new Mmorpg.GameService.Placement.ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                WorkerId,
                WorkerRuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        var ticket = JsonSerializer.Deserialize<WorkerJoinTicket>(
            JsonSerializer.Serialize<ServerJoinTicket>(consumed.Value!, WebJson),
            WebJson)!;
        return ActiveSimulationSession.FromJoinTicket(ticket, DateTime.UtcNow);
    }

    private static async Task<CharacterInventorySnapshotResponse> SnapshotAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static async Task<ItemInstanceSnapshotResponse> FindAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        string definitionId)
    {
        return Items(await SnapshotAsync(context, player))
            .Single(item => item.DefinitionId == definitionId);
    }

    private static IEnumerable<ItemInstanceSnapshotResponse> Items(CharacterInventorySnapshotResponse snapshot)
    {
        return snapshot.PermanentInventory.Slots
            .Select(slot => slot.Item)
            .Concat(snapshot.Equipment.Select(slot => slot.Item))
            .OfType<ItemInstanceSnapshotResponse>();
    }

    // Serves the worker's item-operation calls with the GameService mutation service, the
    // route's request type, and its HTTP result mapping, without a web host.
    private sealed class InProcessItemOperations(PostgresIntegrationTestContext context)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(4, segments.Length);
            Assert.Equal("simulation-sessions", segments[1]);
            Assert.Equal("item-operations", segments[3]);
            var operation = JsonSerializer.Deserialize<ServerItemOperationRequest>(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                WebJson)!;
            await using var scope = context.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<SimulationItemMutationService>()
                .ExecuteAsync(WorkerId, Guid.Parse(segments[2]), operation, cancellationToken);

            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            var body = new MemoryStream();
            httpContext.Response.Body = body;
            await result.ToHttpResult().ExecuteAsync(httpContext);
            var response = new HttpResponseMessage((HttpStatusCode)httpContext.Response.StatusCode)
            {
                Content = new ByteArrayContent(body.ToArray())
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                httpContext.Response.ContentType ?? "application/json");
            return response;
        }
    }
}
