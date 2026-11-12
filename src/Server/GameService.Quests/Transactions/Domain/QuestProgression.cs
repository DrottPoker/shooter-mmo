using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests.Transactions.Domain;

// Quest progress in the quest log: credit for the counted objectives of current steps, and the
// move to the next step once a step is complete. A complete last step waits for the turn-in.
internal static class QuestProgression
{
    // Adds the credit that creditFor gives each counted objective of every active quest's
    // current step, at most up to the objective's count, and returns the changed quests. The
    // catalog check at startup keeps every active quest in the catalog; a quest that is not,
    // such as one that a manual database change left behind, gets no credit and never blocks
    // the credit of the others.
    public static async Task<IReadOnlyList<string>> CreditAsync(
        ItemTransactionContext context,
        IQuestDefinitions questDefinitions,
        ICharacterQuestLog questLog,
        Guid characterId,
        Func<QuestObjectiveDefinition, int> creditFor,
        CancellationToken cancellationToken)
    {
        var log = await questLog.ReadAsync(context.Scope, characterId, cancellationToken);
        var changed = new List<string>();
        foreach (var active in log.ActiveQuests)
        {
            if (questDefinitions.Find(active.QuestId) is not { } quest
                || TryCurrentStep(quest, active) is not { } step)
            {
                context.Journal.AddMetadataAudit(
                    "quest_credit_skipped",
                    null,
                    new { CharacterId = characterId, active.QuestId, active.StepId });
                continue;
            }

            var progress = new Dictionary<string, int>(active.Progress, StringComparer.Ordinal);
            var updates = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var objective in step.Objectives.Where(QuestRules.StoresProgress))
            {
                var credit = creditFor(objective);
                var current = progress.GetValueOrDefault(objective.Id);
                if (credit > 0 && !QuestRules.IsObjectiveComplete(objective, current))
                {
                    updates[objective.Id] = Math.Min(objective.Count, current + credit);
                }
            }

            if (updates.Count == 0)
            {
                continue;
            }

            await questLog.SetProgressAsync(context.Scope, characterId, quest.Id, updates, cancellationToken);
            foreach (var update in updates)
            {
                progress[update.Key] = update.Value;
            }

            await AdvanceIfCompleteAsync(context, questLog, characterId, quest, step, progress, cancellationToken);
            changed.Add(quest.Id);
        }

        return changed;
    }

    // Moves the quest to its next step when the current one is complete. Steps before the last
    // hold counted objectives only, so their stored progress decides.
    public static async Task AdvanceIfCompleteAsync(
        ItemTransactionContext context,
        ICharacterQuestLog questLog,
        Guid characterId,
        QuestDefinition quest,
        QuestStepDefinition step,
        IReadOnlyDictionary<string, int> progress,
        CancellationToken cancellationToken)
    {
        var index = Array.IndexOf(quest.Steps, step);
        if (index == quest.Steps.Length - 1
            || !QuestRules.IsStepComplete(step, objectiveId => progress.GetValueOrDefault(objectiveId)))
        {
            return;
        }

        var next = quest.Steps[index + 1];
        await questLog.AdvanceAsync(
            context.Scope,
            characterId,
            quest.Id,
            next.Id,
            CountedObjectiveIds(next),
            cancellationToken);
    }

    public static QuestStepDefinition CurrentStep(QuestDefinition quest, ActiveQuestState active)
    {
        return TryCurrentStep(quest, active)
            ?? throw new InvalidOperationException(
                $"Active quest '{active.QuestId}' is at step '{active.StepId}', which its definition no longer holds.");
    }

    public static QuestStepDefinition? TryCurrentStep(QuestDefinition quest, ActiveQuestState active)
    {
        return quest.Steps.FirstOrDefault(step => string.Equals(step.Id, active.StepId, StringComparison.Ordinal));
    }

    public static string[] CountedObjectiveIds(QuestStepDefinition step)
    {
        return step.Objectives
            .Where(QuestRules.StoresProgress)
            .Select(objective => objective.Id)
            .ToArray();
    }
}
