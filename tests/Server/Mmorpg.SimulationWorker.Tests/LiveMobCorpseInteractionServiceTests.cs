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
    public async Task EquipmentLootClaimsIntoAnEmptySlotAndNeverSwapsLiveLoot()
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

        var swap = await service.MutateAsync(
            session,
            RealtimeCorpseInteractionIntent.CreateLootToEquipment(
                Guid.NewGuid(),
                corpse.CorpseId,
                corpse.Revision,
                lootEntry.LootEntryId,
                0,
                "ammunition",
                Guid.NewGuid(),
                2),
            CancellationToken.None);
        Assert.Equal("live_mob_corpse_operation_unsupported", swap.Error!.Code);
        Assert.Empty(handler.Bodies);

        await service.MutateAsync(
            session,
            RealtimeCorpseInteractionIntent.CreateLootToEquipment(
                Guid.NewGuid(),
                corpse.CorpseId,
                corpse.Revision,
                lootEntry.LootEntryId,
                0,
                "ammunition",
                Guid.Empty,
                0),
            CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("ammunition", request.RootElement.GetProperty("equipmentSlotId").GetString());
        Assert.Equal(Guid.Empty, request.RootElement.GetProperty("destinationContainerId").GetGuid());
        Assert.Equal(-1, request.RootElement.GetProperty("destinationSlotIndex").GetInt32());
    }

    private static LiveMobCorpseState CreateCorpse()
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
            MobCorpseLifecycleService.DefaultPresentationKey,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            createdAt,
            createdAt.AddMinutes(2),
            [new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.ore", 3, 0)]);
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
}
