using System;
using System.Collections.Generic;

namespace Mmorpg.WorldData.Quests
{
    public enum QuestAvailability
    {
        Available,
        BelowMinimumLevel,
        MissingRequiredQuest,
        AlreadyActive,
        AlreadyCompleted,
        QuestLogFull
    }

    // The quest rules that GameService, SimulationWorker, and the client share, so the client
    // offers exactly what the servers accept and shows the progress they count.
    public static class QuestRules
    {
        // A quest is available when the character is high enough, has completed every required
        // quest, does not have it active, never completed it unless it is repeatable, and has
        // room in its quest log. The first rule that fails names the reason.
        public static QuestAvailability Evaluate(
            QuestDefinition quest,
            int characterLevel,
            int activeQuestCount,
            int maximumActiveQuests,
            Func<string, bool> isActive,
            Func<string, int> completionCount)
        {
            if (quest == null)
            {
                throw new ArgumentNullException(nameof(quest));
            }

            if (isActive == null)
            {
                throw new ArgumentNullException(nameof(isActive));
            }

            if (completionCount == null)
            {
                throw new ArgumentNullException(nameof(completionCount));
            }

            if (isActive(quest.Id))
            {
                return QuestAvailability.AlreadyActive;
            }

            if (!quest.Repeatable && completionCount(quest.Id) > 0)
            {
                return QuestAvailability.AlreadyCompleted;
            }

            foreach (var required in quest.RequiredQuestIds ?? Array.Empty<string>())
            {
                if (completionCount(required) == 0)
                {
                    return QuestAvailability.MissingRequiredQuest;
                }
            }

            if (characterLevel < quest.MinimumLevel)
            {
                return QuestAvailability.BelowMinimumLevel;
            }

            return activeQuestCount >= maximumActiveQuests
                ? QuestAvailability.QuestLogFull
                : QuestAvailability.Available;
        }

        // Progress past the required count completes an objective, so a lowered count in changed
        // content leaves no quest stuck.
        public static bool IsObjectiveComplete(QuestObjectiveDefinition objective, int progress)
        {
            if (objective == null)
            {
                throw new ArgumentNullException(nameof(objective));
            }

            return progress >= objective.Count;
        }

        public static bool IsStepComplete(QuestStepDefinition step, Func<string, int> progressOf)
        {
            if (step == null)
            {
                throw new ArgumentNullException(nameof(step));
            }

            if (progressOf == null)
            {
                throw new ArgumentNullException(nameof(progressOf));
            }

            foreach (var objective in step.Objectives)
            {
                if (!IsObjectiveComplete(objective, progressOf(objective.Id)))
                {
                    return false;
                }
            }

            return true;
        }

        // A quest is ready to turn in once its last step is current and complete.
        public static bool IsReadyToTurnIn(QuestDefinition quest, int currentStepIndex, Func<string, int> progressOf)
        {
            if (quest == null)
            {
                throw new ArgumentNullException(nameof(quest));
            }

            return currentStepIndex == quest.Steps.Length - 1
                && IsStepComplete(quest.Steps[currentStepIndex], progressOf);
        }

        // A credited kill of a Mob counts for a kill objective that names the Mob's actor
        // definition or one of its tags.
        public static bool CountsKill(QuestObjectiveDefinition objective, string mobActorId, IReadOnlyCollection<string> mobTags)
        {
            if (objective == null)
            {
                throw new ArgumentNullException(nameof(objective));
            }

            if (!string.Equals(objective.Kind, QuestObjectiveKindIds.Kill, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var actorId in objective.ActorIds)
            {
                if (string.Equals(actorId, mobActorId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (string.IsNullOrEmpty(objective.ActorTag) || mobTags == null)
            {
                return false;
            }

            foreach (var tag in mobTags)
            {
                if (string.Equals(tag, objective.ActorTag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // A committed yield of a resource node counts for a gather objective that names the
        // node's actor definition.
        public static bool CountsGather(QuestObjectiveDefinition objective, string resourceActorId)
        {
            if (objective == null)
            {
                throw new ArgumentNullException(nameof(objective));
            }

            if (!string.Equals(objective.Kind, QuestObjectiveKindIds.Gather, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var actorId in objective.ActorIds)
            {
                if (string.Equals(actorId, resourceActorId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // A collect objective for a quest item names the Mobs that drop it. Returns the drop source
        // of the Mob's actor definition, or null when the Mob drops nothing for the objective.
        public static QuestDropSourceDefinition FindDropSource(QuestObjectiveDefinition objective, string mobActorId)
        {
            if (objective == null)
            {
                throw new ArgumentNullException(nameof(objective));
            }

            if (!string.Equals(objective.Kind, QuestObjectiveKindIds.Collect, StringComparison.Ordinal)
                || objective.DropSources == null)
            {
                return null;
            }

            foreach (var source in objective.DropSources)
            {
                if (string.Equals(source.ActorId, mobActorId, StringComparison.Ordinal))
                {
                    return source;
                }
            }

            return null;
        }

        // Collect objectives count the carried items, so only the other kinds store progress.
        public static bool StoresProgress(QuestObjectiveDefinition objective)
        {
            if (objective == null)
            {
                throw new ArgumentNullException(nameof(objective));
            }

            return !string.Equals(objective.Kind, QuestObjectiveKindIds.Collect, StringComparison.Ordinal);
        }

        // The objectives a quest asks for at a step: only the current step counts.
        public static IReadOnlyList<QuestObjectiveDefinition> CurrentObjectives(QuestDefinition quest, int currentStepIndex)
        {
            if (quest == null)
            {
                throw new ArgumentNullException(nameof(quest));
            }

            return currentStepIndex >= 0 && currentStepIndex < quest.Steps.Length
                ? quest.Steps[currentStepIndex].Objectives
                : Array.Empty<QuestObjectiveDefinition>();
        }
    }
}
