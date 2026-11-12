using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Quests.Transactions.Domain;

// The items of a quest's lineage through the item primitives: start items and quest drops carry
// the quest as their quest_grant source, and ending the quest removes them with its reason.
// Collect and deliver objectives count and take carried items of the lineage.
internal static class QuestLineage
{
    public static ItemLineage Of(string questId)
    {
        return new ItemLineage(ItemPolicySourceKinds.QuestGrant, questId);
    }

    public static Task<int> CountOwnedAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        string definitionId,
        CancellationToken cancellationToken)
    {
        return LineageItems.CountOwnedAsync(context, characterId, Of(questId), definitionId, cancellationToken);
    }

    // Removes every item with the lineage of an abandoned quest and returns how many items it
    // removed.
    public static Task<int> RemoveAbandonedAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken)
    {
        return LineageItems.RemoveAllAsync(context, characterId, Of(questId), "quest_abandoned", cancellationToken);
    }

    // Removes what is left with the lineage of a turned-in quest, such as spare quest drops.
    public static Task<int> RemoveCompletedAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken)
    {
        return LineageItems.RemoveAllAsync(context, characterId, Of(questId), "quest_completed", cancellationToken);
    }

    // Removes every item with the lineage of a quest that a development reset forgets.
    public static Task<int> RemoveResetAsync(
        ItemTransactionContext context,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken)
    {
        return LineageItems.RemoveAllAsync(context, characterId, Of(questId), "quest_reset", cancellationToken);
    }

    // The carried items that count for a collect or deliver objective of the quest.
    public static Task<int> CountCarriedAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        string questId,
        ResolvedItemDefinition definition,
        CancellationToken cancellationToken)
    {
        return CarriedStacks.CountAsync(context, character, Of(questId), definition, cancellationToken);
    }

    // Takes what an objective asks for from the carried items that count for it, all or nothing.
    public static async Task TakeCarriedAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        string questId,
        ResolvedItemDefinition definition,
        int quantity,
        string auditPrefix,
        CancellationToken cancellationToken)
    {
        if (!await CarriedStacks.TryTakeAsync(
                context,
                character,
                Of(questId),
                definition,
                quantity,
                auditPrefix,
                cancellationToken))
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestItemsMissing,
                "The character does not carry enough of the items the quest asks for.");
        }
    }
}
