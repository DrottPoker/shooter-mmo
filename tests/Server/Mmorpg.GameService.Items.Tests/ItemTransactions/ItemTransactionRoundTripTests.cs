using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using static Mmorpg.GameService.Testing.ItemScenarioWorld;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// The round trips of the item transaction kernel. The goldens pin each operation's round
// trips; the budget here states the ceiling of the most frequent one, so a change cannot
// quietly take the kernel's batches apart. The other tests check that a statement which fails
// inside a batch ends the transaction as it did when it travelled alone.
public sealed class ItemTransactionRoundTripTests
{
    // A worker's relocation between two inventory slots: 33 round trips before the kernel sent
    // its fixed runs together, 23 since.
    private const int RelocationRoundTripBudget = 23;

    private const string ItemLockStatement = "select id from item_instances where id = any($1) order by id for update";

    private static readonly ItemOperationsOptions ShortLockTimeout = ItemOperationsOptions.CreateDefaults() with
    {
        TransactionTimeout = TimeSpan.FromSeconds(2),
        LockTimeout = TimeSpan.FromMilliseconds(100)
    };

    [PostgresIntegrationFact]
    public async Task AWorkerRelocationStaysWithinItsRoundTripBudget()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context, typeof(ItemTransactionRoundTripTests).Assembly);
        var player = await world.RegisterAsync("round_trip_budget");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 5, state.PermanentInventoryContainerId, 0);
        var session = await world.JoinAsync(player);
        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);

        var (result, trace) = await traced.TraceAsync(service => service.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                SimulationActor(session),
                new RelocateItemCommand(
                    characterId,
                    state.Revision,
                    oreId,
                    ore.Revision,
                    state.PermanentInventoryContainerId,
                    1)),
            CancellationToken.None));

        AssertCommitted(result);
        Assert.True(
            trace.RoundTrips <= RelocationRoundTripBudget,
            $"A worker relocation took {trace.RoundTrips} round trips, above its budget of "
            + $"{RelocationRoundTripBudget}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, trace.RoundTripLines));
    }

    // The mutation scope locks containers, items, and policies in one round trip. A lock
    // timeout on the items still ends the transaction with the timeout and releases the claim,
    // so the same operation runs once the lock is free.
    [PostgresIntegrationFact]
    public async Task ALockTimeoutInsideABatchReleasesTheClaim()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(
            context,
            typeof(ItemTransactionRoundTripTests).Assembly,
            ShortLockTimeout);
        var player = await world.RegisterAsync("batch_lock_timeout");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 5, state.PermanentInventoryContainerId, 0);
        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        var request = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForOfflineAccount(player.Registration.AccountId),
            new RelocateItemCommand(
                characterId,
                state.Revision,
                oreId,
                ore.Revision,
                state.PermanentInventoryContainerId,
                1));

        await using (var blocker = await context.DataSource.OpenConnectionAsync())
        {
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await blocker.ExecuteAsync(
                "select id from item_instances where id = @ItemInstanceId for update;",
                new { ItemInstanceId = oreId },
                blockerTransaction);

            var (timedOut, trace) = await traced.TraceAsync(service =>
                service.ExecuteAsync(request, CancellationToken.None));

            Assert.False(timedOut.Succeeded);
            Assert.Equal(ItemTransactionErrorCodes.ItemTransactionTimeout, timedOut.Error!.Code);
            var itemLock = trace.RoundTripLines
                .Select((line, index) => (line, index))
                .Single(entry => entry.line.EndsWith(ItemLockStatement, StringComparison.Ordinal))
                .index;
            Assert.NotEqual(SqlTraceRecorder.RoundTripLine, trace.RoundTripLines[itemLock - 1]);
            Assert.Equal("c1 rollback", trace.RoundTripLines[^1]);
            await blockerTransaction.RollbackAsync();
        }

        await using (var verify = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                0,
                await verify.ExecuteScalarAsync<int>(
                    "select count(*) from item_operations where operation_id = @OperationId;",
                    new { request.OperationId }));
        }

        AssertCommitted(await traced.Service.ExecuteAsync(request, CancellationToken.None));
    }

    // The worker read and the assignment lock share a round trip. When the worker's runtime
    // changed and the assignment lock times out, the worker check, which came first, still
    // decides the answer, as it did when the lock never ran.
    [PostgresIntegrationFact]
    public async Task AFailedWorkerCheckWinsOverTheAssignmentLockOfItsRoundTrip()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(
            context,
            typeof(ItemTransactionRoundTripTests).Assembly,
            ShortLockTimeout);
        var player = await world.RegisterAsync("worker_check_first");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 5, state.PermanentInventoryContainerId, 0);
        var session = await world.JoinAsync(player);
        state = await world.LoadStateAsync(characterId);
        var ore = await world.LoadItemAsync(oreId);
        await world.ExecuteSqlAsync(
            "update simulation_workers set runtime_id = 'replacement-runtime' where id = @WorkerId;",
            new { WorkerId });
        var request = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            SimulationActor(session),
            new RelocateItemCommand(
                characterId,
                state.Revision,
                oreId,
                ore.Revision,
                state.PermanentInventoryContainerId,
                1));

        await using var blocker = await context.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            """
            select id
            from simulation_assignments
            where worker_id = @WorkerId
              and shard_id = @ShardId
              and released_at is null
            for update;
            """,
            new { WorkerId, ShardId },
            blockerTransaction);

        var (result, trace) = await traced.TraceAsync(service =>
            service.ExecuteAsync(request, CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.WorkerRuntimeChanged, result.Error!.Code);
        Assert.Contains(
            trace.RoundTripLines,
            line => line.Contains("for share of assignment", StringComparison.Ordinal));
        await blockerTransaction.RollbackAsync();
    }
}
