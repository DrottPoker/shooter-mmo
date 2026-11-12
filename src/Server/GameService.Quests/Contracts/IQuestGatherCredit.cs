using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Quests.Contracts;

// Counts a gathering yield for the gather objectives of the gathered node's actor definition in
// the current steps of the character's quests, in the yield's item transaction, so a retried
// yield counts once. The result names the quests it changed in its questLog section. A yield
// that no current step asks for changes nothing.
public interface IQuestGatherCredit
{
    Task CreditAsync(
        ItemTransactionContext context,
        Guid characterId,
        string resourceActorId,
        int quantity,
        CancellationToken cancellationToken);
}
