using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Quests.Tests;

// POST /api/simulation-sessions/{simulationSessionId}/quest-log: the worker that hosts a live
// session reads its character's quest log at join; any other credentials are rejected with the
// codes of the other worker reads.
public sealed class SimulationQuestLogServiceIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task TheHostingWorkerReadsTheQuestLogOfItsSession()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var questLog = context.Services.GetRequiredService<ICharacterQuestLog>();
        await QuestLogTestDatabase.WriteAsync(context, scope => questLog.AcceptAsync(
            scope, player.Character.Id, "quest.fixture.wolves", "hunt", ["wolves"], CancellationToken.None));
        var session = await JoinAsync(context, player);

        var result = await ReadAsync(context, WorkerId, session.SimulationSessionId, Request(session));

        Assert.True(result.Succeeded, result.Error?.Message);
        var log = result.Value!;
        Assert.Equal(player.Character.Id, log.CharacterId);
        Assert.Equal(context.Services.GetRequiredService<IQuestDefinitions>().Revision, log.QuestCatalogRevision);
        var active = Assert.Single(log.ActiveQuests);
        Assert.Equal(("quest.fixture.wolves", "hunt"), (active.QuestId, active.StepId));
        Assert.Equal([new QuestObjectiveProgressResponse("wolves", 0)], active.Objectives);
    }

    [PostgresIntegrationFact]
    public async Task OnlyTheCredentialsOfTheLiveSessionReadTheQuestLog()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        var request = Request(session);
        var sessionId = session.SimulationSessionId;

        AssertRejected(
            await ReadAsync(context, "another-simulation-worker", sessionId, request),
            StatusCodes.Status403Forbidden,
            "wrong_simulation_worker");
        AssertRejected(
            await ReadAsync(context, WorkerId, sessionId, request with { ShardId = null }),
            StatusCodes.Status400BadRequest,
            "invalid_simulation_session_request");
        AssertRejected(
            await ReadAsync(context, WorkerId, Guid.NewGuid(), request),
            StatusCodes.Status401Unauthorized,
            "simulation_session_invalid");
        AssertRejected(
            await ReadAsync(context, WorkerId, sessionId, request with { SessionToken = "another-session-token" }),
            StatusCodes.Status401Unauthorized,
            "simulation_session_invalid");
        AssertRejected(
            await ReadAsync(context, WorkerId, sessionId, request with { CharacterId = Guid.NewGuid() }),
            StatusCodes.Status401Unauthorized,
            "simulation_session_invalid");
        AssertRejected(
            await ReadAsync(context, WorkerId, sessionId, request with { WorkerRuntimeId = "another-runtime" }),
            StatusCodes.Status409Conflict,
            "worker_runtime_changed");

        await context.ExecuteAsync(
            "update simulation_assignments set released_at = now() where shard_id = 'local-shard-1';");
        AssertRejected(
            await ReadAsync(context, WorkerId, sessionId, request),
            StatusCodes.Status403Forbidden,
            "wrong_simulation_worker");
    }

    // A worker that runs another quest catalog would count by other rules, so even the worker
    // that hosts the session reads nothing until it runs the catalog GameService runs.
    [PostgresIntegrationFact]
    public async Task AWorkerThatRunsAnotherQuestCatalogReadsNoQuestLog()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        var request = Request(session);

        AssertRejected(
            await ReadAsync(context, WorkerId, session.SimulationSessionId, request with { QuestCatalogRevision = new string('a', 64) }),
            StatusCodes.Status409Conflict,
            QuestCatalogFence.MismatchCode);
        AssertRejected(
            await ReadAsync(context, WorkerId, session.SimulationSessionId, request with { QuestCatalogRevision = null }),
            StatusCodes.Status409Conflict,
            QuestCatalogFence.MismatchCode);
    }

    [PostgresIntegrationFact]
    public async Task PlacementReadsTheMirroredQuestCatalogRevision()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var provider = context.Services.GetRequiredService<IQuestCatalogRevisionProvider>();

        Assert.Equal(
            context.Services.GetRequiredService<IQuestDefinitions>().Revision,
            await ReadRevisionAsync(context, provider));

        // The provider reads the mirror, which the seeder rewrites when the catalog changes.
        var changed = new string('a', 64);
        await context.ExecuteAsync($"update quest_catalogs set revision = '{changed}';");
        Assert.Equal(changed, await ReadRevisionAsync(context, provider));
    }

    private static async Task<ServiceResult<CharacterQuestLogResponse>> ReadAsync(
        PostgresIntegrationTestContext context,
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationQuestLogRequest request)
    {
        await using var scope = context.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SimulationQuestLogService>().ReadAsync(
            authenticatedWorkerId,
            simulationSessionId,
            request,
            CancellationToken.None);
    }

    private static SimulationQuestLogRequest Request(ConsumedSimulationJoinTicketResponse session)
    {
        return new SimulationQuestLogRequest(
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            PostgresIntegrationTestContext.FixtureQuestCatalogRevision);
    }

    private static void AssertRejected(
        ServiceResult<CharacterQuestLogResponse> result,
        int statusCode,
        string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(code, result.Error!.Code);
    }

    private static async Task<string?> ReadRevisionAsync(
        PostgresIntegrationTestContext context,
        IQuestCatalogRevisionProvider provider)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var revision = await provider.GetCurrentRevisionAsync(
            new PostgresTransactionScope(connection, transaction),
            CancellationToken.None);
        await transaction.CommitAsync();
        return revision;
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
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
            new ConsumeSimulationJoinTicketRequest(join.Value!.JoinTicket, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }
}
