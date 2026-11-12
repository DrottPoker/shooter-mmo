namespace Mmorpg.GameService.Items.Contracts;

// Temporary bridge, removed by hardening plan step 3.1d: the gathering command still lives in
// Items, below Quests, so it credits quests through this interface, which Quests implements.
// Once the economy area sits above Quests, it calls a Quests contract directly. The yield counts
// for the gather objectives of the node's actor definition in the yield's transaction, and the
// result names the quests it changed.
public interface IQuestGatherCredit
{
    Task CreditAsync(
        ItemTransactionContext context,
        Guid characterId,
        string resourceActorId,
        int quantity,
        CancellationToken cancellationToken);
}
