using Microsoft.AspNetCore.Http;
using Mmorpg.GameService.Placement.Development;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// The three places where a character's saved place and vitals cross the simulation session API:
// the join ticket consume returns them, a checkpoint saves a batch of them, and a release saves
// the final one. Only the character's active, unreleased session on the worker runtime writes,
// and only a newer state of the same session replaces a stored one.
public sealed class SimulationCharacterStateIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private const string WorldId = "development-world-1";
    private const string CollisionRevision = "integration-collision-revision";

    [PostgresIntegrationFact]
    public async Task TheConsumeResponseCarriesTheSavedStateBitForBit()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var first = await JoinAsync(context, player);
        Assert.Null(first.SavedState);
        var state = new SimulationCharacterStateRequest(
            1,
            0.1f,
            BitConverter.Int32BitsToSingle(0x3F800001),
            -3.3333333f,
            -0f,
            41,
            7);
        var released = await ReleaseAsync(context, first, state);
        Assert.True(released.Succeeded, released.Error?.Message);
        Assert.True(released.Value!.FinalStateSaved);

        var second = await JoinAsync(context, player);
        var saved = second.SavedState;

        Assert.NotNull(saved);
        Assert.Equal((WorldId, CollisionRevision, 41, 7), (saved.WorldId, saved.CollisionRevision, saved.Health, saved.Mana));
        Assert.Equal(
            Bits(state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees),
            Bits(saved.PositionX, saved.PositionY, saved.PositionZ, saved.YawDegrees));
    }

    [PostgresIntegrationFact]
    public async Task ACheckpointSavesTheActiveSessionsAndSkipsEveryOtherEntry()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await context.AddShardAsync("local-shard-3");
        var active = await JoinAsync(context, await context.RegisterPlayerAsync("active@example.com", "active_player", "Active Saver"));
        var stale = await JoinAsync(context, await context.RegisterPlayerAsync("stale@example.com", "stale_player", "Stale Saver"));
        var released = await JoinAsync(context, await context.RegisterPlayerAsync("gone@example.com", "gone_player", "Gone Saver"));
        var elsewhere = await JoinAsync(
            context,
            await context.RegisterPlayerAsync("away@example.com", "away_player", "Away Saver"),
            "local-shard-3",
            "worker-local-shard-3",
            "runtime-local-shard-3");
        var bot = ConsumeBotSession();
        Assert.True((await CheckpointAsync(context, Entry(stale, 5, 1f))).Succeeded);
        Assert.True((await ReleaseAsync(context, released, null)).Succeeded);

        var checkpoint = await CheckpointAsync(
            context,
            Entry(active, 1, 10f, health: 61, mana: 3),
            Entry(stale, 5, 20f),
            Entry(released, 2, 30f),
            Entry(elsewhere, 1, 40f),
            Entry(bot, 1, 50f));

        Assert.True(checkpoint.Succeeded, checkpoint.Error?.Message);
        Assert.Equal((1, 4), (checkpoint.Value!.Accepted, checkpoint.Value.Skipped));
        Assert.Equal("10 61 3", await StoredAsync(context, active.CharacterId));
        Assert.Equal("1 50 40", await StoredAsync(context, stale.CharacterId));
        Assert.Null(await StoredAsync(context, released.CharacterId));
        Assert.Null(await StoredAsync(context, elsewhere.CharacterId));

        // A lower sequence never replaces a newer state; a higher one does.
        var late = await CheckpointAsync(context, Entry(active, 1, 11f), Entry(stale, 4, 21f));
        var newer = await CheckpointAsync(context, Entry(stale, 6, 22f));
        Assert.Equal((0, 2), (late.Value!.Accepted, late.Value.Skipped));
        Assert.Equal((1, 0), (newer.Value!.Accepted, newer.Value.Skipped));
        Assert.Equal("10 61 3", await StoredAsync(context, active.CharacterId));
        Assert.Equal("22 50 40", await StoredAsync(context, stale.CharacterId));
    }

    [PostgresIntegrationFact]
    public async Task ACheckpointOfAnotherRuntimeOrWorkerSavesNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context, await context.RegisterPlayerAsync());
        var service = context.SimulationSessionService;

        var otherRuntime = await service.CheckpointAsync(
            WorkerId,
            new SimulationCharacterCheckpointRequest("another-runtime", [Entry(session, 1, 1f)]),
            CancellationToken.None);
        var otherWorker = await service.CheckpointAsync(
            "another-worker",
            new SimulationCharacterCheckpointRequest(RuntimeId, [Entry(session, 1, 1f)]),
            CancellationToken.None);

        Assert.Equal((0, 1), (otherRuntime.Value!.Accepted, otherRuntime.Value.Skipped));
        Assert.Equal((0, 1), (otherWorker.Value!.Accepted, otherWorker.Value.Skipped));
        Assert.Null(await StoredAsync(context, session.CharacterId));
    }

    [PostgresIntegrationFact]
    public async Task AReleaseStoresItsFinalStateOnlyWhenItEndsTheSession()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context, await context.RegisterPlayerAsync());
        Assert.True((await CheckpointAsync(context, Entry(session, 3, 1f))).Succeeded);

        var stale = await ReleaseAsync(context, session, State(2, 2f), token: "stale-token");
        Assert.Equal(StatusCodes.Status401Unauthorized, stale.StatusCode);
        Assert.Equal(1, await ActiveSessionsAsync(context));
        Assert.Equal("1 50 40", await StoredAsync(context, session.CharacterId));

        // A final state that is not newer than the stored one of the session ends the session
        // and keeps the stored state.
        var older = await ReleaseAsync(context, session, State(2, 3f));
        Assert.True(older.Succeeded, older.Error?.Message);
        Assert.True(older.Value!.Released);
        Assert.False(older.Value.FinalStateSaved);
        Assert.Equal(0, await ActiveSessionsAsync(context));
        Assert.Equal("1 50 40", await StoredAsync(context, session.CharacterId));

        // A repeated release succeeds, but a released session never writes.
        var repeated = await ReleaseAsync(context, session, State(9, 4f));
        Assert.True(repeated.Succeeded, repeated.Error?.Message);
        Assert.False(repeated.Value!.FinalStateSaved);
        Assert.Equal("1 50 40", await StoredAsync(context, session.CharacterId));
    }

    [PostgresIntegrationFact]
    public async Task AReleaseStoresTheFinalStateWithTheRelease()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context, await context.RegisterPlayerAsync());
        Assert.True((await CheckpointAsync(context, Entry(session, 3, 1f))).Succeeded);

        var released = await ReleaseAsync(context, session, State(4, 5f, health: 12, mana: 0));

        Assert.True(released.Succeeded, released.Error?.Message);
        Assert.True(released.Value!.Released);
        Assert.True(released.Value.FinalStateSaved);
        Assert.Equal(0, await ActiveSessionsAsync(context));
        Assert.Equal("5 12 0", await StoredAsync(context, session.CharacterId));
        var checkpointAfterRelease = await CheckpointAsync(context, Entry(session, 5, 6f));
        Assert.Equal((0, 1), (checkpointAfterRelease.Value!.Accepted, checkpointAfterRelease.Value.Skipped));
        Assert.Equal("5 12 0", await StoredAsync(context, session.CharacterId));
    }

    [PostgresIntegrationFact]
    public async Task AReleaseWithoutAFinalStateKeepsTheStoredState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context, await context.RegisterPlayerAsync());
        Assert.True((await CheckpointAsync(context, Entry(session, 1, 7f))).Succeeded);

        var released = await ReleaseAsync(context, session, null);

        Assert.True(released.Succeeded, released.Error?.Message);
        Assert.False(released.Value!.FinalStateSaved);
        Assert.Equal("7 50 40", await StoredAsync(context, session.CharacterId));
    }

    [PostgresIntegrationFact]
    public async Task InvalidStatesAndCheckpointsAreRefusedWithWhatIsWrong()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var session = await JoinAsync(context, await context.RegisterPlayerAsync());
        var service = context.SimulationSessionService;
        var tooMany = Enumerable.Range(0, SimulationCharacterStateRules.MaximumCheckpointStates + 1)
            .Select(_ => Entry(session, 1, 1f) with { SimulationSessionId = Guid.NewGuid() })
            .ToArray();

        var refusals = new[]
        {
            await CheckpointAsync(context),
            await service.CheckpointAsync(WorkerId, new SimulationCharacterCheckpointRequest(RuntimeId, null), CancellationToken.None),
            await service.CheckpointAsync(WorkerId, new SimulationCharacterCheckpointRequest(RuntimeId, tooMany), CancellationToken.None),
            await service.CheckpointAsync(WorkerId, new SimulationCharacterCheckpointRequest("bad runtime", [Entry(session, 1, 1f)]), CancellationToken.None),
            await CheckpointAsync(context, Entry(session, 1, 1f), Entry(session, 2, 2f)),
            await CheckpointAsync(context, Entry(session, 1, 1f) with { SimulationSessionId = Guid.Empty }),
            await CheckpointAsync(context, Entry(session, 0, 1f)),
            await CheckpointAsync(context, Entry(session, 1, float.NaN)),
            await CheckpointAsync(context, Entry(session, 1, 1f) with { PositionZ = float.PositiveInfinity }),
            await CheckpointAsync(context, Entry(session, 1, 2_000_000f)),
            await CheckpointAsync(context, Entry(session, 1, 1f) with { YawDegrees = 361f }),
            await CheckpointAsync(context, Entry(session, 1, 1f, health: 0)),
            await CheckpointAsync(context, Entry(session, 1, 1f, mana: -1)),
            await CheckpointAsync(context, Entry(session, 1, 1f, health: 1_000_001))
        };

        Assert.All(refusals, refusal => Assert.Equal(StatusCodes.Status400BadRequest, refusal.StatusCode));
        Assert.Equal(
            [
                "invalid_character_checkpoint: A checkpoint names 1 to 500 character states.",
                "invalid_character_checkpoint: A checkpoint names 1 to 500 character states.",
                "invalid_character_checkpoint: A checkpoint names 1 to 500 character states.",
                "invalid_simulation_worker_identity: Simulation worker id and runtime id are required.",
                $"invalid_character_checkpoint: The checkpoint names simulation session {session.SimulationSessionId} more than once.",
                "invalid_character_checkpoint: Character state 1 of the checkpoint names no simulation session.",
                "invalid_character_state: Character state 1 of the checkpoint must have a sequence above 0.",
                "invalid_character_state: Character state 1 of the checkpoint must have a finite position within 1000000 meters of the origin.",
                "invalid_character_state: Character state 1 of the checkpoint must have a finite position within 1000000 meters of the origin.",
                "invalid_character_state: Character state 1 of the checkpoint must have a finite position within 1000000 meters of the origin.",
                "invalid_character_state: Character state 1 of the checkpoint must have a finite facing between -360 and 360 degrees.",
                "invalid_character_state: Character state 1 of the checkpoint must have a health between 1 and 1000000.",
                "invalid_character_state: Character state 1 of the checkpoint must have a mana between 0 and 1000000.",
                "invalid_character_state: Character state 1 of the checkpoint must have a health between 1 and 1000000."
            ],
            refusals.Select(refusal => refusal.Error!.Code + ": " + refusal.Error.Message));

        var release = await ReleaseAsync(context, session, State(1, 1f, health: 0));
        Assert.Equal(StatusCodes.Status400BadRequest, release.StatusCode);
        Assert.Equal(
            "invalid_character_state: The final character state must have a health between 1 and 1000000.",
            release.Error!.Code + ": " + release.Error.Message);
        Assert.Equal(1, await ActiveSessionsAsync(context));
        Assert.Null(await StoredAsync(context, session.CharacterId));
    }

    private static SimulationCharacterStateRequest State(long sequence, float x, int health = 50, int mana = 40)
    {
        return new SimulationCharacterStateRequest(sequence, x, 0f, -2f, 90f, health, mana);
    }

    private static SimulationCharacterCheckpointEntry Entry(
        ConsumedSimulationJoinTicketResponse session,
        long sequence,
        float x,
        int health = 50,
        int mana = 40)
    {
        return new SimulationCharacterCheckpointEntry(session.SimulationSessionId, sequence, x, 0f, -2f, 90f, health, mana);
    }

    private static (int, int, int, int) Bits(float x, float y, float z, float yaw)
    {
        return (
            BitConverter.SingleToInt32Bits(x),
            BitConverter.SingleToInt32Bits(y),
            BitConverter.SingleToInt32Bits(z),
            BitConverter.SingleToInt32Bits(yaw));
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        string shardId = ShardId,
        string workerId = WorkerId,
        string runtimeId = RuntimeId)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            shardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(join.Value!.JoinTicket, workerId, runtimeId, shardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    // An in-memory development bot session on this worker runtime, which no database row backs.
    private static ConsumedSimulationJoinTicketResponse ConsumeBotSession()
    {
        var authority = new DevelopmentSimulationBotAuthority(
            new DevelopmentSimulationBotOptions(
                true,
                new string('s', 32),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                "Active Bot",
                0),
            TimeProvider.System);
        var issued = authority.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(Guid.NewGuid(), 1, ShardId),
            new DevelopmentSimulationBotPlacement(
                ShardId,
                WorldId,
                WorkerId,
                RuntimeId,
                "127.0.0.1",
                27015,
                100,
                0,
                0,
                0,
                1,
                "simulation-revision",
                CollisionRevision));
        Assert.True(issued.Succeeded, issued.Error?.Message);
        Assert.True(authority.TryConsumeTicket(
            new ConsumeSimulationJoinTicketRequest(issued.Value!.JoinTicket, WorkerId, RuntimeId, ShardId),
            out var consumed));
        Assert.True(consumed.Value!.IsSyntheticBot);
        Assert.Null(consumed.Value.SavedState);
        return consumed.Value;
    }

    private static Task<ServiceResult<SimulationCharacterCheckpointResponse>> CheckpointAsync(
        PostgresIntegrationTestContext context,
        params SimulationCharacterCheckpointEntry[] entries)
    {
        return context.SimulationSessionService.CheckpointAsync(
            WorkerId,
            new SimulationCharacterCheckpointRequest(RuntimeId, entries),
            CancellationToken.None);
    }

    private static Task<ServiceResult<SimulationSessionLeaseResponse>> ReleaseAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        SimulationCharacterStateRequest? finalState,
        string? token = null)
    {
        return context.SimulationSessionService.ReleaseAsync(
            session.SimulationSessionId,
            WorkerId,
            new SimulationSessionReleaseRequest(RuntimeId, token ?? session.SimulationSessionToken, finalState),
            CancellationToken.None);
    }

    // The stored position x, health, and mana, or null when the character has no state.
    private static Task<string?> StoredAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        return context.ExecuteScalarStringAsync(
            $"select position_x::text || ' ' || health || ' ' || mana from character_simulation_states where character_id = '{characterId}';");
    }

    private static Task<int> ActiveSessionsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from character_simulation_sessions where released_at is null;");
    }
}
