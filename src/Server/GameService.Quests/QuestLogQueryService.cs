using System.Data;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Quests.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Quests;

internal sealed class QuestLogQueryService(
    NpgsqlDataSource dataSource,
    ICharacterQuestLog questLog,
    IQuestDefinitions questDefinitions)
{
    public async Task<ServiceResult<CharacterQuestLogResponse>> GetCharacterQuestLogAsync(
        Guid accountId,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));
            var owned = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                $"""
                select exists (
                    select 1
                    from {CharactersSql.ActiveCharacters} character
                    where character.id = @CharacterId
                      and character.account_id = @AccountId);
                """,
                new { AccountId = accountId, CharacterId = characterId },
                transaction,
                cancellationToken: cancellationToken));
            if (!owned)
            {
                await transaction.CommitAsync(cancellationToken);
                return ServiceResult<CharacterQuestLogResponse>.NotFound(
                    "character_quests_not_found",
                    "Character quests were not found.");
            }

            var state = await questLog.ReadAsync(
                new PostgresTransactionScope(connection, transaction),
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<CharacterQuestLogResponse>.Ok(new CharacterQuestLogResponse(
                characterId,
                questDefinitions.Revision,
                state.Revision,
                state.ActiveQuests
                    .Select(quest => new ActiveQuestResponse(
                        quest.QuestId,
                        quest.StepId,
                        quest.AcceptedAt,
                        quest.Progress
                            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                            .Select(entry => new QuestObjectiveProgressResponse(entry.Key, entry.Value))
                            .ToArray()))
                    .ToArray(),
                state.CompletedQuests
                    .Select(quest => new CompletedQuestResponse(
                        quest.QuestId,
                        quest.CompletionCount,
                        quest.LastCompletedAt))
                    .ToArray()));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
