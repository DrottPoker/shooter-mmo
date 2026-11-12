using Mmorpg.GameService.Testing;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.Testing;
using JoinShardRequest = Mmorpg.GameService.Placement.JoinShardRequest;

namespace Mmorpg.Integration.Tests;

// The worker's own join, checkpoint, and release requests against GameService's routes: a
// character joins, moves and is checkpointed, leaves with its final state, and joins again where
// it left, with the health and mana it left with.
public sealed class CharacterStatePersistenceFlowIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string WorldId = "development-world-1";
    private const string CollisionRevision = "integration-collision-revision";

    [PostgresIntegrationFact]
    public async Task ACharacterJoinsAgainWhereItLeftWithTheHealthItLeftWith()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("persisted@example.com", "persisted_player", "Persisted Hero");
        using var httpClient = new HttpClient(new InProcessWorkerRoutes(context, WorkerId))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var client = new GameServiceClient(httpClient);
        var sessions = new ActiveSimulationSessionStore();
        var releases = new SimulationSessionReleaseService(
            client,
            sessions,
            CharacterSessionStores.Create(new CharacterItemStateStore(), new CharacterLookStore(), new CharacterQuestLogStore()));

        var first = await JoinAsync(context, client, player, cancellationToken);
        Assert.Null(first.SavedState);
        sessions.Register(first);

        // The character walked and took damage; the checkpoint saves it.
        var checkpoint = await client.CheckpointCharacterStatesAsync(
            WorkerId,
            new SimulationCharacterCheckpointRequest(
                WorkerRuntimeId,
                [new SimulationCharacterCheckpointEntry(first.SimulationSessionId, 1, 4.25f, 0f, -7.5f, 135f, 80, 60)]),
            cancellationToken);
        Assert.True(checkpoint.Succeeded, checkpoint.Error?.Code + ": " + checkpoint.Error?.Message);
        Assert.Equal((1, 0), (checkpoint.Value!.Accepted, checkpoint.Value.Skipped));

        // It walked on and lost more health before it left; the leave carries that state.
        var left = new CharacterStateSnapshot(6.5f, BitConverter.Int32BitsToSingle(0x3C23D70A), -9.75f, 270f, 63, 21);
        releases.FinalStates.Capture(first.CharacterId, first.SimulationSessionId, new CharacterFinalState(2, left));
        var released = await releases.ReleaseAsync(first, cancellationToken);
        Assert.True(released.Succeeded, released.Code + ": " + released.Message);
        Assert.Equal(0, releases.FinalStates.Count);

        // A late checkpoint of the ended session changes nothing.
        var late = await client.CheckpointCharacterStatesAsync(
            WorkerId,
            new SimulationCharacterCheckpointRequest(
                WorkerRuntimeId,
                [new SimulationCharacterCheckpointEntry(first.SimulationSessionId, 3, 1f, 0f, 1f, 0f, 99, 99)]),
            cancellationToken);
        Assert.Equal((0, 1), (late.Value!.Accepted, late.Value.Skipped));

        var second = await JoinAsync(context, client, player, cancellationToken);

        var saved = second.SavedState;
        Assert.NotNull(saved);
        // The same World with the collision this worker runs, so the worker restores the place.
        Assert.Equal((WorldId, CollisionRevision), (saved.WorldId, saved.CollisionRevision));
        Assert.Equal(
            (left.PositionX, left.PositionY, left.PositionZ, left.YawDegrees, left.Health, left.Mana),
            (saved.PositionX, saved.PositionY, saved.PositionZ, saved.YawDegrees, saved.Health, saved.Mana));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(left.PositionY),
            BitConverter.SingleToInt32Bits(saved.PositionY));
    }

    // Joins through the worker's consume request, as the worker's join service does.
    private static async Task<ActiveSimulationSession> JoinAsync(
        PostgresIntegrationTestContext context,
        GameServiceClient client,
        IntegrationPlayer player,
        CancellationToken cancellationToken)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            cancellationToken);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await client.ConsumeJoinTicketAsync(
            join.Value!.JoinTicket,
            WorkerId,
            WorkerRuntimeId,
            ShardId,
            cancellationToken);
        Assert.True(consumed.Succeeded, consumed.Error?.Code + ": " + consumed.Error?.Message);
        return ActiveSimulationSession.FromJoinTicket(consumed.Value!, DateTime.UtcNow);
    }
}
