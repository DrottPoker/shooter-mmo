using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameProtocol;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Quests;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.Testing;
using Mmorpg.WorldData.Actors;
using ServerJoinTicket = Mmorpg.GameService.Placement.ConsumedSimulationJoinTicketResponse;
using WorkerJoinTicket = Mmorpg.SimulationWorker.GameServiceApi.ConsumedSimulationJoinTicketResponse;
using WorkerMobLootGrantRequest = Mmorpg.SimulationWorker.GameServiceApi.SimulationMobLootGrantRequest;
using WorkerQuestDrop = Mmorpg.SimulationWorker.GameServiceApi.SimulationQuestDrop;
using WorkerQuestDropRequest = Mmorpg.SimulationWorker.GameServiceApi.SimulationQuestDropRequest;
using WorkerQuestLog = Mmorpg.SimulationWorker.Quests.CharacterQuestLog;
using WorkerQuestLogRequest = Mmorpg.SimulationWorker.GameServiceApi.SimulationQuestLogRequest;
using WorkerQuestLogResponse = Mmorpg.SimulationWorker.GameServiceApi.SimulationQuestLogResponse;

namespace Mmorpg.Integration.Tests;

// The worker's quest log read and quest actions against the GameService quest and item routes,
// through the routes' JSON contracts and HTTP result mapping: the worker reads the joining
// character's quest log, accepts the fixture errand at Mira, which grants its letter, and
// abandons it, which takes the letter back; it counts a credited wolf kill for the hunt and an
// Iron Vein yield for the errand's ore.
public sealed class QuestFlowIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string Errand = "quest.fixture.errand";
    private const string Wolves = "quest.fixture.wolves";
    private const string Trophies = "quest.fixture.trophies";
    private const string Trophy = "fixture.trophy";
    private const string Mira = "npc.city_services";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [PostgresIntegrationFact]
    public async Task TheWorkerKeepsTheQuestLogThroughAnAcceptAndAnAbandon()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("quest-flow@example.com", "quest_flow", "Quest Flow");
        await CompleteWolvesAsync(context, player.Character.Id);
        using var httpClient = Routes(context);
        var worker = await ConnectAsync(context, player, new GameServiceClient(httpClient));
        Assert.Equal(context.Services.GetRequiredService<IQuestDefinitions>().Revision, worker.Read.QuestCatalogRevision);
        Assert.Empty(worker.Read.ActiveQuests);
        Assert.Equal(1, Assert.Single(worker.Read.CompletedQuests).CompletionCount);
        var session = worker.Session;

        var refused = await worker.Executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Errand, "npc.city_guard"),
            cancellationToken);
        Assert.False(refused.Succeeded);
        Assert.Equal("quest_npc_access_required", refused.Code);
        Assert.False(refused.ShouldDisconnect);

        var accepted = await worker.Executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Errand, Mira),
            cancellationToken);
        Assert.True(accepted.Succeeded, accepted.Code + ": " + accepted.Message);
        var letter = Assert.Single(accepted.ItemStateChange!.Items);
        Assert.Equal(("fixture.letter", RealtimeItemStateLocationKind.Container), (letter.DefinitionId, letter.Location));
        Assert.Equal(worker.Read.QuestLogRevision, accepted.QuestLogChange!.PreviousQuestLogRevision);
        var errand = Assert.Single(accepted.QuestLogChange.Quests);
        Assert.Equal((Errand, "work"), (errand.QuestId, errand.StepId));
        Assert.Equal(
            ["hand_over=0", "ore=0", "report=0"],
            errand.Objectives.Select(objective => objective.ObjectiveId + "=" + objective.Progress));

        var abandoned = await worker.Executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Abandon, Errand),
            cancellationToken);
        Assert.True(abandoned.Succeeded, abandoned.Code + ": " + abandoned.Message);
        var removed = Assert.Single(abandoned.ItemStateChange!.Items);
        Assert.Equal((letter.ItemInstanceId, RealtimeItemStateLocationKind.Removed), (removed.ItemInstanceId, removed.Location));
        Assert.Equal(accepted.QuestLogChange.QuestLogRevision, abandoned.QuestLogChange!.PreviousQuestLogRevision);
        Assert.Equal(string.Empty, Assert.Single(abandoned.QuestLogChange.Quests).StepId);

        var kept = worker.Log();
        Assert.Empty(kept.ActiveQuests);
        Assert.Equal(await QuestLogRevisionAsync(context, session.CharacterId), kept.Revision);
        Assert.True(worker.Items.TryGet(session.CharacterId, session.SimulationSessionId, out var carry));
        Assert.Equal(abandoned.ItemStateRevision, carry!.ItemStateRevision);
    }

    // The worker names the credited Mob and its tags, GameService counts the kill once per
    // operation id, and a kill that no step asks for changes nothing.
    [PostgresIntegrationFact]
    public async Task TheWorkerCountsACreditedKillForTheHunt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("quest-kill@example.com", "quest_kill", "Quest Kill");
        using var httpClient = Routes(context);
        var worker = await ConnectAsync(context, player, new GameServiceClient(httpClient));
        var session = worker.Session;
        var accepted = await worker.Executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Wolves, Mira),
            cancellationToken);
        Assert.True(accepted.Succeeded, accepted.Code + ": " + accepted.Message);

        var credit = new QuestOperation(
            Guid.NewGuid(),
            QuestOperationKinds.CreditKill,
            null,
            MobActorId: "mob.feral_wolf",
            MobTags: ["wolf", "beast"],
            ClientIntent: false);
        var counted = await worker.Executor.ExecuteAsync(session, credit, cancellationToken);

        Assert.True(counted.Succeeded, counted.Code + ": " + counted.Message);
        Assert.Null(counted.ItemStateChange);
        var change = counted.QuestLogChange!;
        Assert.Equal((Guid.Empty, accepted.QuestLogChange!.QuestLogRevision), (change.OperationId, change.PreviousQuestLogRevision));
        var hunt = Assert.Single(change.Quests);
        Assert.Equal((Wolves, "hunt"), (hunt.QuestId, hunt.StepId));
        Assert.Equal(["wolves=1"], hunt.Objectives.Select(objective => objective.ObjectiveId + "=" + objective.Progress));

        // A retry of the same credit replays it, and a Grey Wolf counts for no step.
        var replayed = await worker.Executor.ExecuteAsync(session, credit, cancellationToken);
        Assert.True(replayed.Succeeded, replayed.Code + ": " + replayed.Message);
        Assert.Null(replayed.QuestLogChange);
        var grey = await worker.Executor.ExecuteAsync(
            session,
            credit with { OperationId = Guid.NewGuid(), MobActorId = "mob.grey_wolf" },
            cancellationToken);
        Assert.True(grey.Succeeded, grey.Code + ": " + grey.Message);
        Assert.Null(grey.QuestLogChange);

        var kept = worker.Log();
        Assert.Equal(1, kept.ActiveQuests[Wolves].Progress["wolves"]);
        Assert.Equal(await QuestLogRevisionAsync(context, session.CharacterId), kept.Revision);
    }

    // A yield that names the vein counts for the errand's ore in the gathering transaction, and
    // the worker patches its quest log from the item route's answer.
    [PostgresIntegrationFact]
    public async Task TheWorkerFollowsTheErrandOreOfAGatheringYield()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("quest-gather@example.com", "quest_gather", "Quest Gather");
        await CompleteWolvesAsync(context, player.Character.Id);
        await EquipPickaxeAsync(context, player);
        using var httpClient = Routes(context);
        var client = new GameServiceClient(httpClient);
        var worker = await ConnectAsync(context, player, client);
        var session = worker.Session;
        var accepted = await worker.Executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Errand, Mira),
            cancellationToken);
        Assert.True(accepted.Succeeded, accepted.Code + ": " + accepted.Message);

        var mined = await new SimulationGatheringService(client).GatherAsync(
            session,
            new SimulationGatheringRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "resource.mine",
                "fixture.node:0000",
                new WorldActorResourceProfileDefinition
                {
                    Id = "resource.fixture",
                    RequiredToolKind = "pickaxe",
                    YieldDefinitionId = "fixture.ore",
                    YieldQuantity = 1,
                    CycleSeconds = 3f,
                    Charges = 5,
                    RespawnSeconds = 30f
                },
                "resource.iron_vein"),
            cancellationToken);
        Assert.True(mined.Succeeded, mined.Error?.Code + ": " + mined.Error?.Message);

        var change = await worker.Executor.FollowAsync(session, mined.Transaction!.QuestLog, cancellationToken);

        Assert.Equal((Guid.Empty, accepted.QuestLogChange!.QuestLogRevision, false), (change!.OperationId, change.PreviousQuestLogRevision, change.ReloadRequired));
        var errand = Assert.Single(change.Quests);
        Assert.Equal((Errand, "work"), (errand.QuestId, errand.StepId));
        Assert.Contains("ore=1", errand.Objectives.Select(objective => objective.ObjectiveId + "=" + objective.Progress));
        var kept = worker.Log();
        Assert.Equal(1, kept.ActiveQuests[Errand].Progress["ore"]);
        Assert.Equal(await QuestLogRevisionAsync(context, session.CharacterId), kept.Revision);
    }

    // The worker asks which of three rolled trophies the quest still needs, gets the two it asks
    // for, and loots one with the quest's lineage, so the next wolf's trophies come down to the
    // one still missing.
    [PostgresIntegrationFact]
    public async Task TheWorkerLearnsWhichTrophiesTheQuestStillNeedsAndLootsOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("quest-drop@example.com", "quest_drop", "Quest Drop");
        using var httpClient = Routes(context);
        var client = new GameServiceClient(httpClient);
        var worker = await ConnectAsync(context, player, client);
        var session = worker.Session;
        var accepted = await worker.Executor.ExecuteAsync(
            session,
            new QuestOperation(Guid.NewGuid(), QuestOperationKinds.Accept, Trophies, Mira),
            cancellationToken);
        Assert.True(accepted.Succeeded, accepted.Code + ": " + accepted.Message);
        WorkerQuestDrop[] rolled = [new(Trophies, Trophy, 1), new(Trophies, Trophy, 1), new(Trophies, Trophy, 1)];

        var needed = await client.ResolveQuestDropsAsync(
            session.SimulationSessionId,
            QuestDrops(session, rolled),
            cancellationToken);
        Assert.True(needed.Succeeded, needed.Error?.Code + ": " + needed.Error?.Message);
        Assert.Equal([new WorkerQuestDrop(Trophies, Trophy, 1), new WorkerQuestDrop(Trophies, Trophy, 1)], needed.Value!.Drops);

        var snapshot = await SnapshotAsync(context, player);
        var looted = await client.GrantLiveMobLootAsync(
            session.SimulationSessionId,
            new WorkerMobLootGrantRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                snapshot.ItemStateRevision,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                Guid.NewGuid(),
                "mob.feral_wolf",
                Trophy,
                1,
                snapshot.PermanentInventory.ContainerId,
                snapshot.PermanentInventory.Revision,
                snapshot.PermanentInventory.Slots.First(slot => slot.Item is null).SlotIndex,
                QuestId: Trophies),
            cancellationToken);
        Assert.True(looted.Succeeded, looted.Error?.Code + ": " + looted.Error?.Message);

        var stillNeeded = await client.ResolveQuestDropsAsync(
            session.SimulationSessionId,
            QuestDrops(session, rolled),
            cancellationToken);
        Assert.True(stillNeeded.Succeeded, stillNeeded.Error?.Code + ": " + stillNeeded.Error?.Message);
        Assert.Equal([new WorkerQuestDrop(Trophies, Trophy, 1)], stillNeeded.Value!.Drops);
    }

    private static WorkerQuestDropRequest QuestDrops(ActiveSimulationSession session, IReadOnlyList<WorkerQuestDrop> drops)
    {
        return new WorkerQuestDropRequest(
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            PostgresIntegrationTestContext.FixtureQuestCatalogRevision,
            "mob.feral_wolf",
            drops);
    }

    private static HttpClient Routes(PostgresIntegrationTestContext context)
    {
        return new HttpClient(new InProcessWorkerRoutes(context, WorkerId))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
    }

    // Joins the character and gives the worker the stores and the executor it keeps for it,
    // with the quest log read at the join.
    private static async Task<WorkerQuests> ConnectAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        GameServiceClient client)
    {
        var session = await JoinAsync(context, player);
        var read = await client.ReadSimulationQuestLogAsync(
            session.SimulationSessionId,
            new WorkerQuestLogRequest(
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                PostgresIntegrationTestContext.FixtureQuestCatalogRevision),
            CancellationToken.None);
        Assert.True(read.Succeeded, read.Error?.Code + ": " + read.Error?.Message);
        var sessions = new ActiveSimulationSessionStore();
        sessions.Register(session);
        var items = new CharacterItemStateStore();
        items.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterItemState.Create(session.CarryState, []),
            out _);
        var questLogs = new CharacterQuestLogStore();
        questLogs.Register(session.CharacterId, session.SimulationSessionId, WorkerQuestLog.From(read.Value!));
        return new WorkerQuests(
            session,
            read.Value!,
            items,
            questLogs,
            new QuestOperationExecutor(
                client,
                items,
                sessions,
                questLogs,
                QuestCatalogFileLoader.Load(RepositoryPaths.QuestCatalogFixtureRuntimePath)));
    }

    private static async Task CompleteWolvesAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var scope = new PostgresTransactionScope(connection, transaction);
        await questLog.AcceptAsync(scope, characterId, Wolves, "hunt", ["wolves"], CancellationToken.None);
        await questLog.CompleteAsync(scope, characterId, Wolves, CancellationToken.None);
        await transaction.CommitAsync();
    }

    // The fixture pickaxe in the Tool slot, granted and equipped while the character is offline.
    private static async Task EquipPickaxeAsync(PostgresIntegrationTestContext context, IntegrationPlayer player)
    {
        var actor = ItemTransactionActor.ForOfflineAccount(player.Registration.AccountId);
        var snapshot = await SnapshotAsync(context, player);
        var granted = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "fixture.pickaxe",
                    1,
                    snapshot.PermanentInventory.ContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(granted.Succeeded, granted.Error?.Message);
        var pickaxe = Assert.Single(granted.ItemRevisions);
        snapshot = await SnapshotAsync(context, player);
        var equipped = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                actor,
                new EquipItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    pickaxe.ItemInstanceId,
                    pickaxe.Revision,
                    "tool")),
            CancellationToken.None);
        Assert.True(equipped.Succeeded, equipped.Error?.Message);
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

    private static async Task<long> QuestLogRevisionAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await questLog.ReadAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            CancellationToken.None);
        await transaction.CommitAsync();
        return state.Revision;
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

    private sealed record WorkerQuests(
        ActiveSimulationSession Session,
        WorkerQuestLogResponse Read,
        CharacterItemStateStore Items,
        CharacterQuestLogStore QuestLogs,
        QuestOperationExecutor Executor)
    {
        public WorkerQuestLog Log()
        {
            Assert.True(QuestLogs.TryGet(Session.CharacterId, Session.SimulationSessionId, out var log));
            return log!;
        }
    }
}
