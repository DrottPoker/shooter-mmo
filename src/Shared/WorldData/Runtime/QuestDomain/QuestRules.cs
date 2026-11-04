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
