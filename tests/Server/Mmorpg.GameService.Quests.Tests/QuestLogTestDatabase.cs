using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Quests.Tests;

// Runs quest log reads and writes in their own transaction, as a caller of the contract does.
internal static class QuestLogTestDatabase
{
    public static async Task<CharacterQuestLogState> ReadAsync(
        PostgresIntegrationTestContext context,
        ICharacterQuestLog questLog,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await questLog.ReadAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            CancellationToken.None);
        await transaction.CommitAsync();
        return state;
    }

    public static async Task<long> WriteAsync(
        PostgresIntegrationTestContext context,
        Func<PostgresTransactionScope, Task<long>> write)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var revision = await write(new PostgresTransactionScope(connection, transaction));
            await transaction.CommitAsync();
            return revision;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
