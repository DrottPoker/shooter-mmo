using Dapper;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.Tools.StackStressGenerator;
using Npgsql;

namespace Mmorpg.Integration.Tests;

// The load tool's drift check reads GameService's item operations the way its retention task
// decides on them. The operation of a death becomes due only when its death event leaves with its
// closed corpse, long after its own window, so its age is no retention lag; an operation that
// retention left behind past its window is one.
public sealed class StressPostgresSamplerIntegrationTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [PostgresIntegrationFact]
    public async Task TheRetentionLagLeavesOutADeathOperationWhoseEventLeftLateButNotAPlainOperation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("sampler_lag");
        var characterId = player.Character.Id;
        var corpseId = await world.ProcessDeathAsync(characterId);
        await using var connection = await context.DataSource.OpenConnectionAsync(cancellationToken);
        var deathOperationId = await connection.QuerySingleAsync<Guid>(
            "select operation_id from death_events where corpse_id = @CorpseId;",
            new { CorpseId = corpseId });

        // The death event left with its corpse, and the pass has not reached the operation yet.
        await connection.ExecuteAsync(
            """
            update item_operations
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '2 days'
            where operation_id = @OperationId;

            delete from death_events where corpse_id = @CorpseId;
            """,
            new { OperationId = deathOperationId, CorpseId = corpseId });
        var database = new NpgsqlConnectionStringBuilder(context.ConnectionString).Database!;
        await using var sampler = StressPostgresSampler.Create(
            context.ConnectionString,
            database,
            retentionWindowSeconds: Day.TotalSeconds);

        var death = await sampler.SampleDriftAsync(0, 0, cancellationToken);

        Assert.Equal(0d, death.RetentionLagSeconds);

        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 1, inventoryId, 0);
        await connection.ExecuteAsync(
            """
            update item_operations operation
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '1 day 10 minutes'
            where operation.operation_kind = 'grant'
              and exists (
                  select 1
                  from item_operation_changes change
                  where change.operation_id = operation.operation_id
                    and change.item_instance_id = @ItemId);
            """,
            new { ItemId = oreId });

        var plain = await sampler.SampleDriftAsync(0, 0, cancellationToken);

        Assert.InRange(plain.RetentionLagSeconds!.Value, 570d, 660d);
    }
}
