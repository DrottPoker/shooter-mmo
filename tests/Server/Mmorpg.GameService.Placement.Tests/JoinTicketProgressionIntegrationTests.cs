using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Progression;

namespace Mmorpg.GameService.Placement.Tests;

// The join ticket consume tells the worker the character's level and experience as Characters
// reads them, with what the level needs by the progression rules of the content.
public sealed class JoinTicketProgressionIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task TheConsumedTicketCarriesTheCharactersProgression()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var newcomer = await context.RegisterPlayerAsync("newcomer@example.com", "newcomer", "Fresh Newcomer");
        var veteran = await context.RegisterPlayerAsync("veteran@example.com", "veteran", "Seasoned Veteran");
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "update characters set level = 3, experience = 50 where id = @Id;",
                new { veteran.Character.Id });
        }

        var rules = context.Services.GetRequiredService<ProgressionRules>();
        var fresh = await JoinAsync(context, newcomer);
        var seasoned = await JoinAsync(context, veteran);

        Assert.Equal(new SimulationProgression(1, 0, 100, rules.HighestMobLevelWithoutExperience(1)), fresh.Progression);
        Assert.Equal(new SimulationProgression(3, 50, 210, rules.HighestMobLevelWithoutExperience(3)), seasoned.Progression);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            PostgresIntegrationTestContext.CreateJoinRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            JoinTicketProofs.Consume(join.Value!, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }
}
