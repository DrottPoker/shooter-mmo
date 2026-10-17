using Mmorpg.GameService.Items;

namespace Mmorpg.GameService.Testing;

// Quest grant and abandon commands as a system actor, for tests that prepare protected
// quest items without an NPC interaction.
internal sealed class QuestItemService(ItemTransactionService transactionService)
{
    public Task<ItemTransactionResult> GrantRequiredItemAsync(
        Guid operationId,
        Guid characterId,
        long expectedCharacterRevision,
        string definitionId,
        int quantity,
        Guid destinationContainerId,
        int? destinationSlotIndex,
        string questGrantId,
        CancellationToken cancellationToken)
    {
        return transactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    characterId,
                    expectedCharacterRevision,
                    definitionId,
                    quantity,
                    destinationContainerId,
                    destinationSlotIndex,
                    ItemPolicySourceKinds.QuestGrant,
                    questGrantId)),
            cancellationToken);
    }

    public Task<ItemTransactionResult> AbandonAsync(
        Guid operationId,
        Guid characterId,
        long expectedCharacterRevision,
        string questGrantId,
        CancellationToken cancellationToken)
    {
        return transactionService.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(
                    characterId,
                    expectedCharacterRevision,
                    questGrantId)),
            cancellationToken);
    }
}
