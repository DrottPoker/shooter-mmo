using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests.Transactions.Domain;

namespace Mmorpg.GameService.Quests.Transactions.Handlers;

// Removes every protected item of an abandoned quest grant.
internal sealed class QuestItemCommandHandler
    : IItemCommandHandler<AbandonQuestItemsCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AbandonQuestItemsCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSystemAuthority();
        if (string.IsNullOrWhiteSpace(command.QuestGrantId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestGrantInvalid,
                "A quest grant id is required.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [new CharacterLockRequest(command.CharacterId, command.ExpectedCharacterRevision)],
            cancellationToken);
        var questGrantId = command.QuestGrantId.Trim();
        var removed = await QuestLineage.RemoveAbandonedAsync(
            context,
            command.CharacterId,
            questGrantId,
            cancellationToken);
        if (removed == 0)
        {
            var unchanged = new { QuestGrantId = questGrantId, RemovedItemCount = 0 };
            context.Journal.AddMetadataAudit("quest_items_already_absent", unchanged, unchanged);
            return;
        }

        context.Journal.TouchCharacter(command.CharacterId);
    }
}
