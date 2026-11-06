using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    // One objective of a quest's current step with the progress the player sees.
    public sealed class QuestObjectiveLine
    {
        public QuestObjectiveLine(QuestObjectiveDefinition objective, int progress)
        {
            Objective = objective;
            Progress = progress;
            IsComplete = QuestRules.IsObjectiveComplete(objective, progress);
        }

        public QuestObjectiveDefinition Objective { get; }

        public int Progress { get; }

        public bool IsComplete { get; }

        // Such as "Feral Wolves slain 3/5", never past the count.
        public string Text
        {
            get { return Objective.Text + " " + Math.Min(Progress, Objective.Count) + "/" + Objective.Count; }
        }
    }

    // The progress of active quests as the servers count it: a counted objective by the stored
    // progress, and a collect objective by the items the character carries.
    public static class QuestProgress
    {
        public static int StepIndexOf(QuestDefinition quest, string stepId)
        {
            if (quest?.Steps == null || string.IsNullOrEmpty(stepId))
            {
                return -1;
            }

            return Array.FindIndex(quest.Steps, step => string.Equals(step.Id, stepId, StringComparison.Ordinal));
        }

        public static int ProgressOf(
            QuestObjectiveDefinition objective,
            QuestClientActiveQuest active,
            Func<string, int> carriedCount)
        {
            if (!QuestRules.StoresProgress(objective))
            {
                return carriedCount != null ? carriedCount(objective.ItemId) : 0;
            }

            return active != null ? active.ProgressOf(objective.Id) : 0;
        }

        public static IReadOnlyList<QuestObjectiveLine> CurrentObjectives(
            QuestDefinition quest,
            QuestClientActiveQuest active,
            Func<string, int> carriedCount)
        {
            return QuestRules.CurrentObjectives(quest, StepIndexOf(quest, active?.StepId))
                .Select(objective => new QuestObjectiveLine(objective, ProgressOf(objective, active, carriedCount)))
                .ToArray();
        }

        // The first step of a quest on offer: nothing counted yet, but a collect objective counts
        // the items the player already carries, as it will right after the quest is accepted.
        public static IReadOnlyList<QuestObjectiveLine> OfferedObjectives(
            QuestDefinition quest,
            Func<string, int> carriedCount)
        {
            return QuestRules.CurrentObjectives(quest, 0)
                .Select(objective => new QuestObjectiveLine(objective, ProgressOf(objective, null, carriedCount)))
                .ToArray();
        }

        public static bool IsReadyToTurnIn(
            QuestDefinition quest,
            QuestClientActiveQuest active,
            Func<string, int> carriedCount)
        {
            if (quest == null || active == null)
            {
                return false;
            }

            var stepIndex = StepIndexOf(quest, active.StepId);
            return stepIndex >= 0
                && QuestRules.IsReadyToTurnIn(
                    quest,
                    stepIndex,
                    objectiveId => ProgressOf(
                        quest.Steps[stepIndex].Objectives.First(objective =>
                            string.Equals(objective.Id, objectiveId, StringComparison.Ordinal)),
                        active,
                        carriedCount));
        }
    }

    // The carried items that count for a collect objective, as GameService counts them: stacks
    // in the permanent inventory and the equipped Bag, an ordinary item without an active
    // policy and a quest item with the quest grant's lineage. The client does not see which
    // quest a lineage names, so a quest item of another quest would count too; GameService
    // decides.
    public static class QuestCarriedItems
    {
        public const string QuestGrantProtection = "quest_grant";

        public static Func<string, int> Counter(InventoryClientState inventory)
        {
            return definitionId => Count(inventory?.ConfirmedSnapshot, inventory?.Catalog, definitionId);
        }

        // Counts the quest-granted items of a definition that the character owns anywhere the
        // inventory shows: carried, equipped, in the Bank, or in Recovery Storage. GameService
        // grants a start item again only while the character owns it nowhere.
        public static Func<string, int> OwnedQuestGrantCounter(InventoryClientState inventory)
        {
            return definitionId => CountOwnedQuestGrants(inventory?.ConfirmedSnapshot, definitionId);
        }

        public static int CountOwnedQuestGrants(CharacterInventorySnapshot snapshot, string definitionId)
        {
            if (snapshot == null || string.IsNullOrEmpty(definitionId))
            {
                return 0;
            }

            var items = snapshot.PermanentInventory.Slots.Select(slot => slot.Item)
                .Concat(snapshot.EquippedBag?.Contents?.Slots.Select(slot => slot.Item) ?? Array.Empty<InventoryItem>())
                .Concat(snapshot.Equipment.Select(slot => slot.Item))
                .Concat(snapshot.Bank?.Slots.Select(slot => slot.Item) ?? Array.Empty<InventoryItem>())
                .Concat(snapshot.RecoveryStorage?.Deliveries.SelectMany(delivery => delivery.Items.Select(item => item.Item))
                    ?? Array.Empty<InventoryItem>());
            return items
                .Where(item => item != null
                    && string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal)
                    && IsQuestGrant(item))
                .Sum(item => item.Quantity);
        }

        public static int Count(CharacterInventorySnapshot snapshot, ClientItemCatalog catalog, string definitionId)
        {
            if (snapshot == null || string.IsNullOrEmpty(definitionId))
            {
                return 0;
            }

            var questItem = catalog != null
                && catalog.TryGetDefinition(definitionId, out var definition)
                && string.Equals(definition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal);
            var slots = snapshot.PermanentInventory.Slots
                .Concat(snapshot.EquippedBag?.Contents?.Slots ?? Array.Empty<InventorySlot>());
            var count = 0;
            foreach (var slot in slots)
            {
                var item = slot.Item;
                if (item == null || !string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal))
                {
                    continue;
                }

                var counts = questItem
                    ? IsQuestGrant(item)
                    : !item.Policies.Any(IsActive);
                if (counts)
                {
                    count += item.Quantity;
                }
            }

            return count;
        }

        private static bool IsQuestGrant(InventoryItem item)
        {
            return item.Policies.Any(policy => IsActive(policy)
                && string.Equals(policy.ProtectionSource, QuestGrantProtection, StringComparison.Ordinal));
        }

        private static bool IsActive(InventoryPolicy policy)
        {
            return string.Equals(policy.Status, ItemPolicyRules.ActiveStatus, StringComparison.Ordinal);
        }
    }
}
