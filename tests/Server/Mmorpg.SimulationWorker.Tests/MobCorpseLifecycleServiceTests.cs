using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Mmorpg.Server.Common.Identity;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class MobCorpseLifecycleServiceTests
{
    [Fact]
    public async Task LiveMobCreationUsesDefinitionLifetimeAndDeterministicGrantIds()
    {
        var runtime = WorldActorTestData.Compile();
        var runtimeIds = Enumerable.Range(1, runtime.SpawnInstances.Length)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var runtimeIndex = 0;
        var actorStore = new WorldActorStore(
            runtime,
            new SimulationEntityRegistry(),
            () => runtimeIds[runtimeIndex++]);
        var identity = new SimulationWorkerIdentity("phase14-runtime", DateTime.UtcNow);
        var actors = actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            identity.RuntimeId);
        var wolf = actors.First(actor => actor.Definition.Id == "mob.feral_wolf");
        // The wolf dies facing away from its spawn yaw.
        wolf.SetPose(wolf.PositionX, wolf.PositionY, wolf.PositionZ, 137f);
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var liveStore = new LiveMobCorpseStore(timeProvider);
        var service = new MobCorpseLifecycleService(
            actorStore,
            liveStore,
            new DurableCorpseStore(timeProvider),
            new GameServiceClient(new HttpClient(new UnexpectedHttpHandler())
            {
                BaseAddress = new Uri("http://localhost")
            }),
            new ActiveSimulationSessionStore(),
            LoadConfig(),
            identity,
            timeProvider,
            RealtimeServerHarness.QuestCatalog);
        var deathEventId = Guid.NewGuid();
        var corpseId = Guid.NewGuid();
        var request = new CreateMobCorpseRequest(
            deathEventId,
            corpseId,
            corpseId,
            wolf.RuntimeActorId,
            [new MobCorpseLootSeed("fixture.ore", 2)]);

        var first = await service.CreateAsync(request, CancellationToken.None);
        var replay = await service.CreateAsync(request, CancellationToken.None);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.NotNull(first.LiveCorpse);
        Assert.Null(first.DurableCorpse);
        Assert.Equal(TimeSpan.FromSeconds(120),
            first.LiveCorpse.ExpiresAt - first.LiveCorpse.CreatedAt);
        Assert.Equal(corpseId, first.LiveCorpse.CorpseId);
        Assert.Equal(first.LiveCorpse.CorpseId, replay.LiveCorpse!.CorpseId);
        Assert.Equal(
            Assert.Single(first.LiveCorpse.Loot).GrantId,
            Assert.Single(replay.LiveCorpse.Loot).GrantId);
        Assert.Equal(first.LiveCorpse, Assert.Single(liveStore.ListActive()));
        // The corpse lies as the wolf fell: its own model, turned as it died.
        Assert.Equal("corpse." + wolf.Definition.PresentationArchetypeId, first.LiveCorpse.PresentationKey);
        Assert.Equal(137f, first.LiveCorpse.YawDegrees);
    }

    // A durable corpse, as a boss keeps, goes to GameService with its owner and loot group.
    [Fact]
    public async Task ADurableMobCorpseNamesItsOwnerToGameService()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.Actors.Single(actor => actor.Id == "mob.feral_wolf").CorpsePersistenceMode = "durable";
        var actorStore = new WorldActorStore(runtime, new SimulationEntityRegistry(), Guid.NewGuid);
        var identity = new SimulationWorkerIdentity("tagging-runtime", DateTime.UtcNow);
        var actors = actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            identity.RuntimeId);
        var wolf = actors.First(actor => actor.Definition.Id == "mob.feral_wolf");
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var handler = new RecordingHttpHandler();
        var sessions = new ActiveSimulationSessionStore();
        var service = new MobCorpseLifecycleService(
            actorStore,
            new LiveMobCorpseStore(timeProvider),
            new DurableCorpseStore(timeProvider),
            new GameServiceClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            }),
            sessions,
            LoadConfig(),
            identity,
            timeProvider,
            RealtimeServerHarness.QuestCatalog);
        var tagger = Guid.NewGuid();

        var lootGroupId = Guid.NewGuid();

        await service.CreateAsync(
            new CreateMobCorpseRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                lootGroupId,
                wolf.RuntimeActorId,
                [new MobCorpseLootSeed("fixture.ore", 2)],
                tagger),
            CancellationToken.None);

        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(tagger, body.RootElement.GetProperty("ownerCharacterId").GetGuid());
        Assert.Equal(lootGroupId, body.RootElement.GetProperty("lootGroupId").GetGuid());
    }

    // The owner's quest drops follow the Mob's own loot, as much of them as GameService says the
    // owner's quests still need, asked through the owner's current session.
    [Fact]
    public async Task ALiveCorpseHoldsTheQuestDropsItsOwnerStillNeeds()
    {
        var handler = new ScriptedHttpHandler(_ => Json(new SimulationQuestDropResponse(
            [new SimulationQuestDrop(Trophies, Trophy, 1)])));
        var (service, wolf, liveStore, sessions) = CreateService(handler, "live");
        var owner = RealtimeTestSessions.Create();
        sessions.Register(owner);
        // A reconnect of the same session brings a new token.
        sessions.Register(owner with { SimulationSessionToken = "reconnected-session-token" });
        var corpseId = Guid.NewGuid();

        var result = await service.CreateAsync(
            OwnedRequest(corpseId, wolf, owner.CharacterId, [new MobCorpseLootSeed("fixture.ore", 2)], TrophyDrops(2)),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(
            [("fixture.ore", 2, 0, (string?)null), (Trophy, 1, 1, Trophies)],
            result.LiveCorpse!.Loot.Select(entry => (entry.DefinitionId, entry.Quantity, entry.SlotIndex, entry.QuestId)));
        Assert.Equal(
            ScopedGuid.Create("mob-loot-grant:1", corpseId),
            result.LiveCorpse.Loot[1].GrantId);
        Assert.Equal(owner.CharacterId, result.LiveCorpse.OwnerCharacterId);
        Assert.Equal([new MobCorpseQuestDropSeed(Trophies, Trophy, 1)], result.QuestDrops);
        var (path, body) = Assert.Single(handler.Requests);
        Assert.Equal($"/api/simulation-sessions/{owner.SimulationSessionId}/quest-drops", path);
        var read = ReadQuestDrops(body);
        Assert.Equal(
            (owner.AccountId, owner.CharacterId, owner.WorkerId, owner.WorkerRuntimeId, owner.ShardId, "reconnected-session-token", "mob.feral_wolf"),
            (read.AccountId, read.CharacterId, read.WorkerId, read.WorkerRuntimeId, read.ShardId, read.SessionToken, read.SourceActorDefinitionId));
        Assert.Equal(TrophyDrops(2).Select(drop => new SimulationQuestDrop(drop.QuestId, drop.DefinitionId, drop.Quantity)), read.Drops);

        // A replay finds the corpse as it was laid out and asks nothing.
        var replay = await service.CreateAsync(
            OwnedRequest(corpseId, wolf, owner.CharacterId, [new MobCorpseLootSeed("fixture.ore", 2)], TrophyDrops(2)),
            CancellationToken.None);
        Assert.Same(result.LiveCorpse, replay.LiveCorpse);
        Assert.Equal(result.QuestDrops, replay.QuestDrops);
        Assert.Single(handler.Requests);
        Assert.Same(result.LiveCorpse, Assert.Single(liveStore.ListActive()));
    }

    // GameService first keeps the quest drops that the owner still needs, and only those take the
    // entries that the Mob's own loot leaves, in request order.
    [Fact]
    public async Task NeededQuestDropsFillTheRoomThatTheMobsOwnLootLeaves()
    {
        var handler = new ScriptedHttpHandler(_ => Json(new SimulationQuestDropResponse(
            [new SimulationQuestDrop(Trophies, Trophy, 1), new SimulationQuestDrop(Trophies, Trophy, 1)])));
        var (service, wolf, _, sessions) = CreateService(handler, "live");
        var owner = RealtimeTestSessions.Create();
        sessions.Register(owner);
        var ownLoot = Enumerable.Range(0, 23).Select(_ => new MobCorpseLootSeed("fixture.ore", 1)).ToArray();
        MobCorpseQuestDropSeed[] rolled = [new("quest.fixture.wolves", Trophy, 1), .. TrophyDrops(2)];

        var result = await service.CreateAsync(
            OwnedRequest(Guid.NewGuid(), wolf, owner.CharacterId, ownLoot, rolled),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(24, result.LiveCorpse!.Loot.Count);
        Assert.Equal((Trophy, Trophies, 23), (result.LiveCorpse.Loot[23].DefinitionId, result.LiveCorpse.Loot[23].QuestId, result.LiveCorpse.Loot[23].SlotIndex));
        Assert.Equal(3, ReadQuestDrops(Assert.Single(handler.Requests).Body).Drops.Count);
    }

    [Fact]
    public async Task QuestDropsThatTheOwnerNoLongerNeedsLeaveNoCorpse()
    {
        var handler = new ScriptedHttpHandler(_ => Json(new SimulationQuestDropResponse([])));
        var (service, wolf, liveStore, sessions) = CreateService(handler, "live");
        var owner = RealtimeTestSessions.Create();
        sessions.Register(owner);

        var result = await service.CreateAsync(
            OwnedRequest(Guid.NewGuid(), wolf, owner.CharacterId, [], TrophyDrops(1)),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Null(result.LiveCorpse);
        Assert.Null(result.DurableCorpse);
        Assert.Empty(liveStore.ListActive());
    }

    // The read changes nothing, so a lost answer fails the creation, which the Mob retries. An
    // owner whose session ended, or who has none on the worker, gets no quest drops, and the Mob's
    // own loot stays.
    [Fact]
    public async Task ALostQuestDropReadFailsTheCreationAndAnOwnerWithoutASessionGetsNone()
    {
        var answers = new Queue<Func<HttpResponseMessage>>(
        [
            () => throw new HttpRequestException("GameService is down."),
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = JsonContent.Create(new { code = "simulation_session_invalid", detail = "The session ended." })
            }
        ]);
        var handler = new ScriptedHttpHandler(_ => answers.Dequeue()());
        var (service, wolf, _, sessions) = CreateService(handler, "live");
        var owner = RealtimeTestSessions.Create();
        sessions.Register(owner);
        var request = OwnedRequest(Guid.NewGuid(), wolf, owner.CharacterId, [new MobCorpseLootSeed("fixture.ore", 1)], TrophyDrops(1));

        var lost = await service.CreateAsync(request, CancellationToken.None);
        var ended = await service.CreateAsync(request, CancellationToken.None);
        sessions.Remove(owner.CharacterId, owner.SimulationSessionId, owner.SimulationSessionToken);
        var left = await service.CreateAsync(
            OwnedRequest(Guid.NewGuid(), wolf, owner.CharacterId, [new MobCorpseLootSeed("fixture.ore", 1)], TrophyDrops(1)),
            CancellationToken.None);

        Assert.Equal("auth_service_unavailable", lost.Error?.Code);
        Assert.True(ended.Succeeded, ended.Error?.Message);
        Assert.Equal(["fixture.ore"], ended.LiveCorpse!.Loot.Select(entry => entry.DefinitionId));
        Assert.True(left.Succeeded, left.Error?.Message);
        Assert.Equal(["fixture.ore"], left.LiveCorpse!.Loot.Select(entry => entry.DefinitionId));
        Assert.Equal(2, handler.Requests.Count);
    }

    // A durable corpse hands every rolled quest drop to GameService, which keeps what the owner
    // needs in the room the Mob's own loot leaves, and creates no corpse when nothing is left.
    [Fact]
    public async Task ADurableCorpseHandsItsQuestDropsToGameService()
    {
        var handler = new ScriptedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = JsonContent.Create(new { code = "mob_corpse_empty", detail = "No loot is left for the durable Mob corpse." })
        });
        var (service, wolf, _, _) = CreateService(handler, "durable");
        var owner = Guid.NewGuid();
        var ownLoot = Enumerable.Range(0, 23).Select(_ => new MobCorpseLootSeed("fixture.ore", 1)).ToArray();

        var result = await service.CreateAsync(
            OwnedRequest(Guid.NewGuid(), wolf, owner, ownLoot, TrophyDrops(3)),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Null(result.DurableCorpse);
        var (path, body) = Assert.Single(handler.Requests);
        Assert.EndsWith("/mob-corpses/durable", path);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var loot = document.RootElement.GetProperty("loot").EnumerateArray().ToArray();
        Assert.Equal(26, loot.Length);
        Assert.False(loot[22].TryGetProperty("questId", out _));
        Assert.All(loot[23..], entry => Assert.Equal(Trophies, entry.GetProperty("questId").GetString()));
        Assert.Equal(Trophy, loot[23].GetProperty("definitionId").GetString());
        Assert.Equal(owner, document.RootElement.GetProperty("ownerCharacterId").GetGuid());
    }

    // GameService names the quest drops that a durable corpse kept, which its owner is then told
    // about. An answer that names a drop the worker never asked for is refused.
    [Theory]
    [InlineData(Trophies, Trophy, true)]
    [InlineData("quest.fixture.wolves", Trophy, false)]
    [InlineData(Trophies, "fixture.ore", false)]
    public async Task ADurableCorpseReportsTheQuestDropsGameServiceKept(string questId, string definitionId, bool valid)
    {
        ScriptedHttpHandler handler = null!;
        handler = new ScriptedHttpHandler(_ => Json(DurableAnswer(
            handler.Requests[^1].Body,
            [new SimulationQuestDrop(questId, definitionId, 1)])));
        var (service, wolf, _, _) = CreateService(handler, "durable");

        var result = await service.CreateAsync(
            OwnedRequest(Guid.NewGuid(), wolf, Guid.NewGuid(), [new MobCorpseLootSeed("fixture.ore", 2)], TrophyDrops(2)),
            CancellationToken.None);

        if (valid)
        {
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.NotNull(result.DurableCorpse);
            Assert.Equal([new MobCorpseQuestDropSeed(Trophies, Trophy, 1)], result.QuestDrops);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Empty(result.QuestDrops);
        }
    }

    private const string Trophies = "quest.fixture.trophies";
    private const string Trophy = "fixture.trophy";

    // GameService's answer to a durable corpse request: the corpse as asked for, holding every
    // requested entry, and the given kept quest drops.
    private static PersistentMobCorpseResponse DurableAnswer(string body, IReadOnlyList<SimulationQuestDrop> kept)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<CreatePersistentMobCorpseRequest>(
            body,
            System.Text.Json.JsonSerializerOptions.Web)!;
        var createdAt = DateTime.UtcNow;
        return new PersistentMobCorpseResponse(
            request.OperationId,
            new DurableCorpseResponse(
                request.CorpseId,
                null,
                null,
                request.ShardId,
                request.PositionX,
                request.PositionY,
                request.PositionZ,
                request.RotationX,
                request.RotationY,
                request.RotationZ,
                request.RotationW,
                request.PresentationKey,
                1,
                createdAt,
                createdAt.AddSeconds(request.LifetimeSeconds),
                false,
                [
                    new CorpseSectionResponse("general_inventory", Guid.NewGuid(), 1, request.Loot.Count),
                    new CorpseSectionResponse("equipment", Guid.NewGuid(), 1, 0),
                    new CorpseSectionResponse("bag", Guid.NewGuid(), 1, 0)
                ])
            {
                SourceActorDefinitionId = request.SourceActorDefinitionId,
                OwnerCharacterId = request.OwnerCharacterId,
                LootGroupId = request.LootGroupId
            })
        {
            QuestDrops = kept
        };
    }

    private static MobCorpseQuestDropSeed[] TrophyDrops(int count)
    {
        return Enumerable.Range(0, count).Select(_ => new MobCorpseQuestDropSeed(Trophies, Trophy, 1)).ToArray();
    }

    private static CreateMobCorpseRequest OwnedRequest(
        Guid corpseId,
        WorldActorRuntimeState wolf,
        Guid ownerCharacterId,
        IReadOnlyList<MobCorpseLootSeed> loot,
        IReadOnlyList<MobCorpseQuestDropSeed> questDrops)
    {
        return new CreateMobCorpseRequest(Guid.NewGuid(), corpseId, corpseId, wolf.RuntimeActorId, loot, ownerCharacterId)
        {
            QuestDrops = questDrops
        };
    }

    private static SimulationQuestDropRequest ReadQuestDrops(string body)
    {
        return System.Text.Json.JsonSerializer.Deserialize<SimulationQuestDropRequest>(
            body,
            System.Text.Json.JsonSerializerOptions.Web)!;
    }

    // A wolf of the test World with the given corpse persistence, on a service that talks to the
    // handler and finds owners in the session store.
    private static (MobCorpseLifecycleService Service, WorldActorRuntimeState Wolf, LiveMobCorpseStore LiveStore, ActiveSimulationSessionStore Sessions)
        CreateService(HttpMessageHandler handler, string persistenceMode)
    {
        var runtime = WorldActorTestData.Compile();
        runtime.Actors.Single(actor => actor.Id == "mob.feral_wolf").CorpsePersistenceMode = persistenceMode;
        var actorStore = new WorldActorStore(runtime, new SimulationEntityRegistry(), Guid.NewGuid);
        var identity = new SimulationWorkerIdentity("quest-drop-runtime", DateTime.UtcNow);
        var wolf = actorStore
            .ActivateAssignment("development-world-1", "local-shard-1", identity.RuntimeId)
            .First(actor => actor.Definition.Id == "mob.feral_wolf");
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var liveStore = new LiveMobCorpseStore(timeProvider);
        var sessions = new ActiveSimulationSessionStore();
        var service = new MobCorpseLifecycleService(
            actorStore,
            liveStore,
            new DurableCorpseStore(timeProvider),
            new GameServiceClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            }),
            sessions,
            LoadConfig(),
            identity,
            timeProvider,
            RealtimeServerHarness.QuestCatalog);
        return (service, wolf, liveStore, sessions);
    }

    private static HttpResponseMessage Json<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private static SimulationWorkerConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(RepositoryPaths.Root)
            .AddJsonFile("src/Server/SimulationWorker/Config/appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SIMULATION_WORKER_SERVICE_SECRET"] =
                    "phase-fourteen-unit-test-secret-at-least-32-characters",
                ["ConnectionStrings:Redis"] = "localhost:6379"
            })
            .Build();
        return SimulationWorkerConfig.FromConfiguration(configuration);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    // Records every request's path and body and answers it from the script.
    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
