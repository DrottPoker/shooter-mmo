using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.Quests;

// Whether a credited kill or a gathering yield may count for a character's quests, by the quest
// log the worker keeps: an unfinished counted objective of a current step asks for it. GameService
// decides; the filter only spares it a credit that counts for nothing. A stale log may miss an
// accepted quest, so GameService then decides every time. Synchronized through the quest logs.
public sealed class QuestProgressFilter
{
    private readonly CharacterQuestLogStore questLogs;
    private readonly Dictionary<string, QuestDefinition> quests;

    public QuestProgressFilter(CharacterQuestLogStore questLogs, QuestCatalogRuntimeDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.questLogs = questLogs ?? throw new ArgumentNullException(nameof(questLogs));
        quests = catalog.Quests.ToDictionary(quest => quest.Id, StringComparer.Ordinal);
    }

    public bool CountsKill(ActiveSimulationSession session, string mobActorId, IReadOnlyCollection<string> mobTags)
    {
        return Counts(session, objective => QuestRules.CountsKill(objective, mobActorId, mobTags));
    }

    public bool CountsGather(ActiveSimulationSession session, string resourceActorId)
    {
        return Counts(session, objective => QuestRules.CountsGather(objective, resourceActorId));
    }

    private bool Counts(ActiveSimulationSession session, Func<QuestObjectiveDefinition, bool> counts)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsSyntheticBot
            || !questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out var log))
        {
            return false;
        }

        if (questLogs.IsStale(session.CharacterId, session.SimulationSessionId))
        {
            return true;
        }

        foreach (var (questId, active) in log!.ActiveQuests)
        {
            var step = quests.TryGetValue(questId, out var quest)
                ? Array.Find(quest.Steps, value => string.Equals(value.Id, active.StepId, StringComparison.Ordinal))
                : null;
            if (step is null)
            {
                continue;
            }

            foreach (var objective in step.Objectives)
            {
                if (QuestRules.StoresProgress(objective)
                    && counts(objective)
                    && !QuestRules.IsObjectiveComplete(objective, active.Progress.GetValueOrDefault(objective.Id)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
