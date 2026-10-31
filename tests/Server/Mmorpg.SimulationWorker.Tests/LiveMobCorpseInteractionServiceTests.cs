using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class LiveMobCorpseInteractionServiceTests
{
    [Fact]
    public async Task LootClaimSendsTheCurrentItemStateRevisionAndRefreshesOnUnknownOutcome()
    {
        var handler = new CapturingUnavailableHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var corpseStore = new LiveMobCorpseStore(TimeProvider.System);
        var itemStateStore = new CharacterItemStateStore();
        var service = new LiveMobCorpseInteractionService(
            corpseStore,
            new GameServiceClient(httpClient),
            itemStateStore);
        var corpse = corpseStore.CreateOrGet(CreateCorpse());
        var lootEntry = corpse.Loot.Single();
        var session = CreateSession(new PlayerCarryState(3, 0, 200));
        itemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState),
            out _);
        // An item operation after the join advanced the committed revision.
        itemStateStore.ApplyCommitted(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(new PlayerCarryState(7, 10, 200)),
            out _);

        var result = await service.MutateAsync(
            session,
            RealtimeCorpseInteractionIntent.CreateLootItem(
                Guid.NewGuid(),
                corpse.CorpseId,
                corpse.Revision,
                lootEntry.LootEntryId,
                0,
                Guid.NewGuid(),
                1,
                0,
                Guid.Empty,
                0),
            CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(7, request.RootElement.GetProperty("expectedCharacterRevision").GetInt64());
        Assert.Equal("auth_service_unavailable", result.Error!.Code);
        Assert.True(result.RequiresInventoryRefresh);
        Assert.True(result.RequiresCorpseRefresh);
    }

    [Fact]
    public async Task EquipmentLootNamesTheEquipmentSlotAndTheStackItTopsUp()
    {
        var handler = new CapturingUnavailableHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var corpseStore = new LiveMobCorpseStore(TimeProvider.System);
        var itemStateStore = new CharacterItemStateStore();
        var service = new LiveMobCorpseInteractionService(
            corpseStore,
            new GameServiceClient(httpClient),
            itemStateStore);
        var corpse = corpseStore.CreateOrGet(CreateCorpse());
        var lootEntry = corpse.Loot.Single();
        var session = CreateSession(new PlayerCarryState(3, 0, 200));
        itemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState),
            out _);
        var equippedArrows = Guid.NewGuid();

        await service.MutateAsync(
            session,
            RealtimeCorpseInteractionIntent.CreateLootToEquipment(
                Guid.NewGuid(),
                corpse.CorpseId,
                corpse.Revision,
                lootEntry.LootEntryId,
                0,
                "ammunition",
                equippedArrows,
                2),
            CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("ammunition", request.RootElement.GetProperty("equipmentSlotId").GetString());
        Assert.Equal(Guid.Empty, request.RootElement.GetProperty("destinationContainerId").GetGuid());
        Assert.Equal(-1, request.RootElement.GetProperty("destinationSlotIndex").GetInt32());
        Assert.Equal(equippedArrows, request.RootElement.GetProperty("targetItemInstanceId").GetGuid());
        Assert.Equal(2, request.RootElement.GetProperty("expectedTargetItemRevision").GetInt64());
    }
    [Fact]
    public async Task LootAllSendsEveryEntryInSlotOrderUnderTheIntentOperationId()
    {
        var handler = new CapturingUnavailableHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var corpseStore = new LiveMobCorpseStore(TimeProvider.System);
        var itemStateStore = new CharacterItemStateStore();
        var service = new LiveMobCorpseInteractionService(
            corpseStore,
            new GameServiceClient(httpClient),
            itemStateStore);
        var corpse = corpseStore.CreateOrGet(CreateCorpse(
            new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.arrow", 8, 1),
            new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.ore", 3, 0)));
        var session = CreateSession(new PlayerCarryState(4, 0, 200));
        itemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState),
            out _);
        var intent = RealtimeCorpseInteractionIntent.CreateLootAll(Guid.NewGuid(), corpse.CorpseId, corpse.Revision);

        var result = await service.MutateAsync(session, intent, CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(intent.OperationId, request.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(4, request.RootElement.GetProperty("expectedCharacterRevision").GetInt64());
        Assert.Equal(
            ["fixture.ore", "fixture.arrow"],
            request.RootElement.GetProperty("loot").EnumerateArray()
                .Select(entry => entry.GetProperty("definitionId").GetString())
                .ToArray());
        Assert.Equal("auth_service_unavailable", result.Error!.Code);
        Assert.Equal(2, corpseStore.CreateSnapshot(corpse.CorpseId)!.Sections[0].Slots.Count);
    }

    [Fact]
    public async Task LootAllOfAStaleCorpseRevisionAsksForARefreshWithoutCallingGameService()
    {
        var handler = new CapturingUnavailableHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var corpseStore = new LiveMobCorpseStore(TimeProvider.System);
        var service = new LiveMobCorpseInteractionService(
            corpseStore,
            new GameServiceClient(httpClient),
            new CharacterItemStateStore());
        var corpse = corpseStore.CreateOrGet(CreateCorpse());

        var result = await service.MutateAsync(
            CreateSession(new PlayerCarryState(1, 0, 200)),
            RealtimeCorpseInteractionIntent.CreateLootAll(Guid.NewGuid(), corpse.CorpseId, corpse.Revision + 1),
            CancellationToken.None);

        Assert.Empty(handler.Bodies);
        Assert.Equal("item_state_conflict", result.Error!.Code);
        Assert.True(result.RequiresCorpseRefresh);
    }

    [Fact]
    public async Task ASingleLootThatLootAllAlreadyGrantedLeavesTheCorpse()
    {
        using var httpClient = new HttpClient(new ProblemHandler("item_already_looted"))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var corpseStore = new LiveMobCorpseStore(TimeProvider.System);
        var itemStateStore = new CharacterItemStateStore();
        var service = new LiveMobCorpseInteractionService(
            corpseStore,
            new GameServiceClient(httpClient),
            itemStateStore);
        var corpse = corpseStore.CreateOrGet(CreateCorpse());
        var lootEntry = corpse.Loot.Single();
        var session = CreateSession(new PlayerCarryState(3, 0, 200));
        itemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState),
            out _);

        var result = await service.MutateAsync(
            session,
            RealtimeCorpseInteractionIntent.CreateLootItem(
                Guid.NewGuid(),
                corpse.CorpseId,
                corpse.Revision,
                lootEntry.LootEntryId,
                0,
                Guid.NewGuid(),
                1,
                0,
                Guid.Empty,
                0),
            CancellationToken.None);

        Assert.Equal("item_already_looted", result.Error!.Code);
        Assert.True(result.RequiresCorpseRefresh);
        Assert.True(corpseStore.IsEmptied(corpse.CorpseId));
    }

    private static LiveMobCorpseState CreateCorpse(params LiveMobCorpseLootEntry[] loot)
    {
        var createdAt = DateTime.UtcNow;
        return new LiveMobCorpseState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "mob.feral_wolf",
            "Feral Wolf",
            "local-shard-1",
            0d,
            0d,
            0d,
            0f,
            MobCorpseLifecycleService.DefaultPresentationKey,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            createdAt,
            createdAt.AddMinutes(2),
            loot.Length > 0 ? loot : [new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.ore", 3, 0)]);
    }

    private static ActiveSimulationSession CreateSession(PlayerCarryState carryState)
    {
        return new ActiveSimulationSession(
            Guid.NewGuid(),
            "live-loot-session-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Live Looter",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "runtime-1",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            carryState,
            false);
    }

    private sealed class CapturingUnavailableHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            throw new HttpRequestException("GameService is unreachable.");
        }
    }

    // Answers every request with a GameService rejection of the given code.
    private sealed class ProblemHandler(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new GameServiceProblemDetails(code, "Rejected.", null))
            });
        }
    }
}
