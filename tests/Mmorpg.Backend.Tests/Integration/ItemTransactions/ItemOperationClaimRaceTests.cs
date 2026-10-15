using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.Testing;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Operation retention can delete a claim row between the conflicting insert and the
// read of that row. The claim then reports a retryable conflict instead of failing.
public sealed class ItemOperationClaimRaceTests
{
    [PostgresIntegrationFact]
    public async Task ConflictingClaimThatDisappearsIsARetryableRejection()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("claim_race");
        var state = await world.LoadStateAsync(player.Character.Id);
        var request = new ItemTransactionRequest<GrantItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSystem(),
            new GrantItemCommand(
                player.Character.Id,
                state.Revision,
                "missing.claim_race.definition",
                1,
                state.PermanentInventoryContainerId,
                0));
        var stored = await context.ItemTransactionService.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, stored.Error!.Code);

        await using var traced = TracedItemTransactions.Create(context);
        var deleted = 0;
        traced.Recorder.OnStatement = statement =>
        {
            if (statement.Contains("from item_operations where operation_id = $1 for update", StringComparison.Ordinal))
            {
                // Retention removes the row after the insert saw the conflict.
                using var connection = context.DataSource.OpenConnection();
                deleted += connection.Execute(
                    "delete from item_operations where operation_id = @OperationId;",
                    new { request.OperationId });
            }
        };
        traced.Recorder.Start();
        var result = await traced.Service.ExecuteAsync(request, CancellationToken.None);
        traced.Recorder.Stop();

        Assert.Equal(1, deleted);
        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemTransactionTimeout, result.Error!.Code);
        Assert.Equal(
            "The item transaction was aborted by concurrent database work. Retry it.",
            result.Error.Message);
        await using var verify = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            0,
            await verify.ExecuteScalarAsync<int>(
                "select count(*) from item_operations where operation_id = @OperationId;",
                new { request.OperationId }));
    }
}
