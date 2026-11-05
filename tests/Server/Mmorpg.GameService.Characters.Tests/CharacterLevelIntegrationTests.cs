using Dapper;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Characters.Tests;

// The character list and a new character name the level, which the client reads for quest
// availability.
public sealed class CharacterLevelIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task TheCharacterListNamesEachCharactersLevel()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "update characters set level = 4 where id = @Id;",
                new { player.Character.Id });
        }

        var listed = await context.CharacterService.ListAsync(player.Registration.AccountId, CancellationToken.None);

        Assert.Equal(1, player.Character.Level);
        Assert.Equal(4, Assert.Single(listed.Value!).Level);
    }
}
