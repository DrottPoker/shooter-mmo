using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests.Transactions.Domain;

// The quest side of a gathering yield, which Economy's gathering command calls through
// IQuestGatherCredit: the yield counts for the gather objectives of the gathered node's actor
// definition in the yield's transaction, so a retried yield counts once, and the result names
// the quests it changed.
internal sealed class QuestGatherCredit(
    IQuestDefinitions questDefinitions,
    ICharacterQuestLog questLog) : IQuestGatherCredit
{
    private readonly IQuestDefinitions questDefinitions = questDefinitions;
    private readonly ICharacterQuestLog questLog = questLog;

    public async Task CreditAsync(
        ItemTransactionContext context,
        Guid characterId,
        string resourceActorId,
        int quantity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resourceActorId);
        var changed = await QuestProgression.CreditAsync(
            context,
            questDefinitions,
            questLog,
            characterId,
            objective => QuestRules.CountsGather(objective, resourceActorId) ? quantity : 0,
            cancellationToken);
        if (changed.Count > 0)
        {
            await QuestLogChanges.ReportAsync(context, questLog, characterId, changed, cancellationToken);
        }
    }
}
