using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.WorldData.Quests
{
    // Compiles the authored quests against the compiled item catalog and the compiled actors of
    // every World. It collects every problem before it fails, so one run shows them all, and equal
    // input compiles to byte-for-byte equal output. The errors name their field by index, such as
    // quests[2].steps[0].objectives[1].count, so the content tools can point at it.
    public static class QuestCatalogCompiler
    {
        private static readonly HashSet<string> QuestCapabilityKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            WorldActorCapabilityKindIds.QuestOffer,
            WorldActorCapabilityKindIds.QuestTurnIn
        };

        public static QuestCatalogRuntimeDocument Compile(
            QuestCatalogAuthoringDocument authoring,
            ItemCatalogRuntimeDocument items,
            IReadOnlyList<WorldActorRuntimeDocument> worlds)
        {
            if (authoring == null)
            {
                throw new ArgumentNullException(nameof(authoring));
            }

            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (worlds == null)
            {
                throw new ArgumentNullException(nameof(worlds));
            }

            var errors = new List<string>();
            var references = new QuestReferences(items, worlds);
            ValidateCatalog(authoring, errors);
            var quests = authoring.Quests ?? Array.Empty<QuestAuthoringEntry>();
            var retired = new HashSet<string>(authoring.RetiredQuestIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var ids = ValidateIds(quests, retired, errors);
            for (var index = 0; index < quests.Length; index++)
            {
                ValidateQuest(quests[index], Path(index), ids, retired, references, errors);
            }

            ValidateRequirementCycles(quests, errors);
            if (errors.Count > 0)
            {
                throw new QuestValidationException(errors);
            }

            var runtime = new QuestCatalogRuntimeDocument
            {
                FormatVersion = QuestCatalogFormat.Version,
                CatalogId = authoring.CatalogId,
                MaximumActiveQuests = authoring.MaximumActiveQuests,
                RetiredQuestIds = retired.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                Quests = quests
                    .Select(CompileQuest)
                    .OrderBy(quest => quest.Id, StringComparer.Ordinal)
                    .ToArray()
            };
            runtime.Revision = ComputeRevision(runtime);
            return runtime;
        }

        // A loaded catalog must be exactly what the compiler wrote: its format, every structural
        // fingerprint, and the revision are computed again and compared.
        public static void ValidateRuntime(QuestCatalogRuntimeDocument runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            var errors = new List<string>();
            if (runtime.FormatVersion != QuestCatalogFormat.Version)
            {
                errors.Add("runtime quest catalog formatVersion must be " + QuestCatalogFormat.Version + ".");
            }

            if (runtime.Quests == null || runtime.RetiredQuestIds == null)
            {
                errors.Add("runtime quest catalog must list its quests and retired quest ids.");
                throw new QuestValidationException(errors);
            }

            foreach (var quest in runtime.Quests)
            {
                if (quest == null || !string.Equals(quest.StructuralFingerprint, StructuralFingerprintOf(quest), StringComparison.Ordinal))
                {
                    errors.Add("runtime quest " + quest?.Id + " has a stale structural fingerprint.");
                }
            }

            if (errors.Count == 0 && !string.Equals(runtime.Revision, ComputeRevision(runtime), StringComparison.Ordinal))
            {
                errors.Add("runtime quest catalog revision does not match its content.");
            }

            if (errors.Count > 0)
            {
                throw new QuestValidationException(errors);
            }
        }

        private static void ValidateCatalog(QuestCatalogAuthoringDocument authoring, List<string> errors)
        {
            if (authoring.FormatVersion != QuestCatalogFormat.Version)
            {
                errors.Add("formatVersion must be " + QuestCatalogFormat.Version + ".");
            }

            if (!IsIdentifier(authoring.CatalogId))
            {
                errors.Add("catalogId must be a content identifier.");
            }

            if (authoring.MaximumActiveQuests < QuestContentRules.MinimumActiveQuestLimit
                || authoring.MaximumActiveQuests > QuestContentRules.MaximumActiveQuestLimit)
            {
                errors.Add("maximumActiveQuests must be between "
                    + QuestContentRules.MinimumActiveQuestLimit
                    + " and "
                    + QuestContentRules.MaximumActiveQuestLimit
                    + ".");
            }

            if (authoring.RetiredQuestIds == null)
            {
                errors.Add("retiredQuestIds is required.");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in authoring.RetiredQuestIds)
            {
                if (!ContentLayout.IsValidDefinitionId(id))
                {
                    errors.Add("retiredQuestIds contains invalid id '" + id + "'.");
                }
                else if (!seen.Add(id))
                {
                    errors.Add("retiredQuestIds contains duplicate id '" + id + "'.");
                }
            }
        }

        private static HashSet<string> ValidateIds(
            QuestAuthoringEntry[] quests,
            HashSet<string> retired,
            List<string> errors)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < quests.Length; index++)
            {
                var quest = quests[index];
                if (quest == null)
                {
                    errors.Add(Path(index) + " is required.");
                    continue;
                }

                if (!ContentLayout.IsValidDefinitionId(quest.Id))
                {
                    errors.Add(Path(index) + ".id must be a lowercase content identifier.");
                    continue;
                }

                if (retired.Contains(quest.Id))
                {
                    errors.Add(Path(index) + ".id '" + quest.Id + "' is retired and can never be used again.");
                }

                if (!ids.Add(quest.Id))
                {
                    errors.Add("quests contains duplicate id '" + quest.Id + "'.");
                }
            }

            return ids;
        }

        private static void ValidateQuest(
            QuestAuthoringEntry quest,
            string path,
            HashSet<string> ids,
            HashSet<string> retired,
            QuestReferences references,
            List<string> errors)
        {
            if (quest == null)
            {
                return;
            }

            ValidateText(quest.Title, QuestContentRules.MaximumTitleLength, path + ".title", errors);
            references.ValidateQuestNpc(quest.GiverActorId, path + ".giverActorId", errors);
            references.ValidateQuestNpc(quest.TurnInActorId, path + ".turnInActorId", errors);
            if (quest.MinimumLevel < 1 || quest.MinimumLevel > references.MaximumCharacterLevel)
            {
                errors.Add(path + ".minimumLevel must be between 1 and " + references.MaximumCharacterLevel + ".");
            }

            ValidateRequiredQuests(quest, path, ids, retired, errors);
            ValidateIdentifierSet(quest.Tags, QuestContentRules.MaximumTags, path + ".tags", errors);
            ValidateText(quest.OfferText, QuestContentRules.MaximumOfferTextLength, path + ".offerText", errors);
            ValidateText(quest.ProgressText, QuestContentRules.MaximumProgressTextLength, path + ".progressText", errors);
            ValidateText(quest.CompletionText, QuestContentRules.MaximumCompletionTextLength, path + ".completionText", errors);
            var startItems = ValidateItemList(
                quest.StartItems,
                QuestContentRules.MaximumStartItems,
                path + ".startItems",
                errors,
                (item, itemPath) => references.ValidateQuestItem(item, itemPath, errors));
            ValidateSteps(quest, path, references, errors);
            ValidateItemConsumption(quest, path, startItems, references, errors);
            if (quest.RewardGold < 0 || quest.RewardGold > QuestContentRules.MaximumRewardGold)
            {
                errors.Add(path + ".rewardGold must be between 0 and " + QuestContentRules.MaximumRewardGold + ".");
            }

            ValidateItemList(
                quest.RewardItems,
                QuestContentRules.MaximumRewardItems,
                path + ".rewardItems",
                errors,
                (item, itemPath) => references.ValidateRewardItem(item, itemPath, errors));
            ValidateItemList(
                quest.RewardChoiceItems,
                QuestContentRules.MaximumRewardChoices,
                path + ".rewardChoiceItems",
                errors,
                (item, itemPath) => references.ValidateRewardItem(item, itemPath, errors));
            if (quest.RewardChoiceItems != null
                && quest.RewardChoiceItems.Length > 0
                && quest.RewardChoiceItems.Length < QuestContentRules.MinimumRewardChoices)
            {
                errors.Add(path + ".rewardChoiceItems must be empty or offer at least "
                    + QuestContentRules.MinimumRewardChoices
                    + " items to choose from.");
            }
        }

        private static void ValidateRequiredQuests(
            QuestAuthoringEntry quest,
            string path,
            HashSet<string> ids,
            HashSet<string> retired,
            List<string> errors)
        {
            if (quest.RequiredQuestIds == null)
            {
                errors.Add(path + ".requiredQuestIds is required.");
                return;
            }

            if (quest.RequiredQuestIds.Length > QuestContentRules.MaximumRequiredQuests)
            {
                errors.Add(path + ".requiredQuestIds may name at most " + QuestContentRules.MaximumRequiredQuests + " quests.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < quest.RequiredQuestIds.Length; index++)
            {
                var required = quest.RequiredQuestIds[index];
                var requiredPath = path + ".requiredQuestIds[" + Index(index) + "]";
                if (string.Equals(required, quest.Id, StringComparison.Ordinal))
                {
                    errors.Add(requiredPath + " cannot require the quest itself.");
                }
                else if (required != null && retired.Contains(required))
                {
                    errors.Add(requiredPath + " names retired quest '" + required + "'.");
                }
                else if (required == null || !ids.Contains(required))
                {
                    errors.Add(requiredPath + " names missing id '" + required + "'.");
                }
                else if (!seen.Add(required))
                {
                    errors.Add(path + ".requiredQuestIds contains duplicate id '" + required + "'.");
                }
            }
        }

        private static void ValidateSteps(
            QuestAuthoringEntry quest,
            string path,
            QuestReferences references,
            List<string> errors)
        {
            if (quest.Steps == null || quest.Steps.Length == 0 || quest.Steps.Length > QuestContentRules.MaximumSteps)
            {
                errors.Add(path + ".steps must hold between 1 and " + QuestContentRules.MaximumSteps + " steps.");
                return;
            }

            var stepIds = new HashSet<string>(StringComparer.Ordinal);
            var objectiveIds = new HashSet<string>(StringComparer.Ordinal);
            for (var stepIndex = 0; stepIndex < quest.Steps.Length; stepIndex++)
            {
                var step = quest.Steps[stepIndex];
                var stepPath = path + ".steps[" + Index(stepIndex) + "]";
                if (step == null)
                {
                    errors.Add(stepPath + " is required.");
                    continue;
                }

                if (!IsIdentifier(step.Id))
                {
                    errors.Add(stepPath + ".id must be a content identifier.");
                }
                else if (!stepIds.Add(step.Id))
                {
                    errors.Add(path + ".steps contains duplicate id '" + step.Id + "'.");
                }

                if (step.Objectives == null
                    || step.Objectives.Length == 0
                    || step.Objectives.Length > QuestContentRules.MaximumObjectivesPerStep)
                {
                    errors.Add(stepPath + ".objectives must hold between 1 and "
                        + QuestContentRules.MaximumObjectivesPerStep
                        + " objectives.");
                    continue;
                }

                var lastStep = stepIndex == quest.Steps.Length - 1;
                for (var objectiveIndex = 0; objectiveIndex < step.Objectives.Length; objectiveIndex++)
                {
                    var objective = step.Objectives[objectiveIndex];
                    var objectivePath = stepPath + ".objectives[" + Index(objectiveIndex) + "]";
                    if (objective == null)
                    {
                        errors.Add(objectivePath + " is required.");
                        continue;
                    }

                    if (!IsIdentifier(objective.Id))
                    {
                        errors.Add(objectivePath + ".id must be a content identifier.");
                    }
                    else if (!objectiveIds.Add(objective.Id))
                    {
                        errors.Add(path + " contains duplicate objective id '" + objective.Id + "'.");
                    }

                    ValidateObjective(quest, objective, objectivePath, lastStep, references, errors);
                }
            }
        }

        private static void ValidateObjective(
            QuestAuthoringEntry quest,
            QuestObjectiveAuthoringEntry objective,
            string path,
            bool lastStep,
            QuestReferences references,
            List<string> errors)
        {
            ValidateText(objective.Text, QuestContentRules.MaximumObjectiveTextLength, path + ".text", errors);
            if (objective.Count < 1 || objective.Count > QuestContentRules.MaximumObjectiveCount)
            {
                errors.Add(path + ".count must be between 1 and " + QuestContentRules.MaximumObjectiveCount + ".");
            }

            var actorIds = objective.ActorIds ?? Array.Empty<string>();
            var dropSources = objective.DropSources ?? Array.Empty<QuestDropSourceAuthoringEntry>();
            if (objective.ActorIds == null)
            {
                errors.Add(path + ".actorIds is required.");
            }

            if (objective.DropSources == null)
            {
                errors.Add(path + ".dropSources is required.");
            }

            switch (objective.Kind)
            {
                case QuestObjectiveKindIds.Kill:
                    ValidateKill(objective, actorIds, dropSources, path, references, errors);
                    break;
                case QuestObjectiveKindIds.Collect:
                    ValidateCollect(objective, actorIds, dropSources, path, lastStep, references, errors);
                    break;
                case QuestObjectiveKindIds.Deliver:
                    ValidateDeliver(quest, objective, actorIds, dropSources, path, lastStep, references, errors);
                    break;
                case QuestObjectiveKindIds.TalkTo:
                    ValidateTalkTo(quest, objective, actorIds, dropSources, path, lastStep, references, errors);
                    break;
                case QuestObjectiveKindIds.Gather:
                    ValidateGather(objective, actorIds, dropSources, path, references, errors);
                    break;
                default:
                    errors.Add(path + ".kind has unsupported value '" + objective.Kind + "'.");
                    break;
            }
        }

        private static void ValidateKill(
            QuestObjectiveAuthoringEntry objective,
            string[] actorIds,
            QuestDropSourceAuthoringEntry[] dropSources,
            string path,
            QuestReferences references,
            List<string> errors)
        {
            var hasTag = !string.IsNullOrEmpty(objective.ActorTag);
            if (hasTag == (actorIds.Length > 0))
            {
                errors.Add(path + " must name either actorIds or one actorTag.");
            }

            if (hasTag)
            {
                references.ValidateMobTag(objective.ActorTag, path + ".actorTag", errors);
            }

            ValidateActorIds(actorIds, path, errors, (actorId, actorPath) => references.ValidatePlacedActor(
                actorId,
                WorldActorKindIds.Mob,
                actorPath,
                errors));
            RequireEmpty(objective.ItemId, path + ".itemId", errors);
            RequireEmpty(objective.NpcActorId, path + ".npcActorId", errors);
            RequireEmpty(objective.Line, path + ".line", errors);
            RequireNoDropSources(dropSources, path, errors);
        }

        private static void ValidateCollect(
            QuestObjectiveAuthoringEntry objective,
            string[] actorIds,
            QuestDropSourceAuthoringEntry[] dropSources,
            string path,
            bool lastStep,
            QuestReferences references,
            List<string> errors)
        {
            if (!lastStep)
            {
                errors.Add(path + " collects items, so it belongs to the last step, whose turn-in takes them.");
            }

            var item = references.ValidateItem(objective.ItemId, path + ".itemId", errors);
            RequireNoActors(actorIds, objective.ActorTag, path, errors);
            RequireEmpty(objective.NpcActorId, path + ".npcActorId", errors);
            RequireEmpty(objective.Line, path + ".line", errors);
            if (item == null || dropSources.Length == 0)
            {
                return;
            }

            // A quest drop is looted with the quest's lineage, which only a protected quest item keeps.
            if (!QuestReferences.IsProtectedQuestItem(item))
            {
                errors.Add(path + ".dropSources may only drop protected quest items.");
                return;
            }

            ValidateDropSources(dropSources, item, path, references, errors);
        }

        private static void ValidateDeliver(
            QuestAuthoringEntry quest,
            QuestObjectiveAuthoringEntry objective,
            string[] actorIds,
            QuestDropSourceAuthoringEntry[] dropSources,
            string path,
            bool lastStep,
            QuestReferences references,
            List<string> errors)
        {
            references.ValidateItem(objective.ItemId, path + ".itemId", errors);
            references.ValidateQuestNpc(objective.NpcActorId, path + ".npcActorId", errors);
            if (lastStep && string.Equals(objective.NpcActorId, quest.TurnInActorId, StringComparison.Ordinal))
            {
                errors.Add(path + " delivers to the turn-in NPC in the last step, where a collect objective belongs.");
            }

            RequireNoActors(actorIds, objective.ActorTag, path, errors);
            RequireEmpty(objective.Line, path + ".line", errors);
            RequireNoDropSources(dropSources, path, errors);
        }

        // Collect objectives count the same carried items, so every item has at most one. The
        // quest items that deliveries and the turn-in take come from the start items, except
        // that a collect objective may also drop its item.
        private static void ValidateItemConsumption(
            QuestAuthoringEntry quest,
            string path,
            Dictionary<string, int> startItems,
            QuestReferences references,
            List<string> errors)
        {
            var objectives = EnumerateObjectives(quest, path).ToArray();
            var delivered = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var entry in objectives)
            {
                var objective = entry.Objective;
                if (string.Equals(objective.Kind, QuestObjectiveKindIds.Deliver, StringComparison.Ordinal)
                    && references.IsQuestItem(objective.ItemId))
                {
                    delivered[objective.ItemId] = (delivered.TryGetValue(objective.ItemId, out var count) ? count : 0)
                        + objective.Count;
                }
            }

            var collectors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in objectives)
            {
                var objective = entry.Objective;
                if (string.IsNullOrEmpty(objective.ItemId))
                {
                    continue;
                }

                var granted = startItems.TryGetValue(objective.ItemId, out var startQuantity) ? startQuantity : 0;
                var deliveredQuantity = delivered.TryGetValue(objective.ItemId, out var deliveries) ? deliveries : 0;
                if (string.Equals(objective.Kind, QuestObjectiveKindIds.Collect, StringComparison.Ordinal))
                {
                    if (collectors.TryGetValue(objective.ItemId, out var firstCollector))
                    {
                        errors.Add(entry.Path + ".itemId '" + objective.ItemId + "' is already collected by objective '"
                            + firstCollector + "', and both would count the same items.");
                        continue;
                    }

                    collectors.Add(objective.ItemId, objective.Id);
                    var drops = objective.DropSources != null && objective.DropSources.Length > 0;
                    if (references.IsQuestItem(objective.ItemId) && !drops && deliveredQuantity + objective.Count > granted)
                    {
                        errors.Add(entry.Path + " collects quest item '" + objective.ItemId
                            + "', which the quest neither drops nor grants as often as it takes it.");
                    }
                }
                else if (string.Equals(objective.Kind, QuestObjectiveKindIds.Deliver, StringComparison.Ordinal)
                    && references.IsQuestItem(objective.ItemId)
                    && deliveredQuantity > granted)
                {
                    errors.Add(entry.Path + " delivers quest item '" + objective.ItemId
                        + "', which the quest does not grant as often as its deliveries take it.");
                }
            }
        }

        private static IEnumerable<(QuestObjectiveAuthoringEntry Objective, string Path)> EnumerateObjectives(
            QuestAuthoringEntry quest,
            string path)
        {
            if (quest.Steps == null)
            {
                yield break;
            }

            for (var stepIndex = 0; stepIndex < quest.Steps.Length; stepIndex++)
            {
                var objectives = quest.Steps[stepIndex]?.Objectives;
                if (objectives == null)
                {
                    continue;
                }

                for (var objectiveIndex = 0; objectiveIndex < objectives.Length; objectiveIndex++)
                {
                    if (objectives[objectiveIndex] != null)
                    {
                        yield return (
                            objectives[objectiveIndex],
                            path + ".steps[" + Index(stepIndex) + "].objectives[" + Index(objectiveIndex) + "]");
                    }
                }
            }
        }

        private static void ValidateTalkTo(
            QuestAuthoringEntry quest,
            QuestObjectiveAuthoringEntry objective,
            string[] actorIds,
            QuestDropSourceAuthoringEntry[] dropSources,
            string path,
            bool lastStep,
            QuestReferences references,
            List<string> errors)
        {
            references.ValidateQuestNpc(objective.NpcActorId, path + ".npcActorId", errors);
            ValidateText(objective.Line, QuestContentRules.MaximumLineLength, path + ".line", errors);
            if (objective.Count != 1)
            {
                errors.Add(path + ".count must be 1 for a conversation.");
            }

            if (lastStep && string.Equals(objective.NpcActorId, quest.TurnInActorId, StringComparison.Ordinal))
            {
                errors.Add(path + " talks to the turn-in NPC in the last step, which the turn-in already does.");
            }

            RequireNoActors(actorIds, objective.ActorTag, path, errors);
            RequireEmpty(objective.ItemId, path + ".itemId", errors);
            RequireNoDropSources(dropSources, path, errors);
        }

        private static void ValidateGather(
            QuestObjectiveAuthoringEntry objective,
            string[] actorIds,
            QuestDropSourceAuthoringEntry[] dropSources,
            string path,
            QuestReferences references,
            List<string> errors)
        {
            if (actorIds.Length == 0)
            {
                errors.Add(path + ".actorIds must name the resource nodes to gather from.");
            }

            ValidateActorIds(actorIds, path, errors, (actorId, actorPath) => references.ValidatePlacedActor(
                actorId,
                WorldActorKindIds.ResourceNode,
                actorPath,
                errors));
            RequireEmpty(objective.ActorTag, path + ".actorTag", errors);
            RequireEmpty(objective.ItemId, path + ".itemId", errors);
            RequireEmpty(objective.NpcActorId, path + ".npcActorId", errors);
            RequireEmpty(objective.Line, path + ".line", errors);
            RequireNoDropSources(dropSources, path, errors);
        }

        private static void ValidateDropSources(
            QuestDropSourceAuthoringEntry[] dropSources,
            ItemDefinition item,
            string path,
            QuestReferences references,
            List<string> errors)
        {
            if (dropSources.Length > QuestContentRules.MaximumDropSources)
            {
                errors.Add(path + ".dropSources may list at most " + QuestContentRules.MaximumDropSources + " sources.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < dropSources.Length; index++)
            {
                var source = dropSources[index];
                var sourcePath = path + ".dropSources[" + Index(index) + "]";
                if (source == null)
                {
                    errors.Add(sourcePath + " is required.");
                    continue;
                }

                references.ValidatePlacedActor(source.ActorId, WorldActorKindIds.Mob, sourcePath + ".actorId", errors);
                if (source.ActorId != null && !seen.Add(source.ActorId))
                {
                    errors.Add(path + ".dropSources contains duplicate id '" + source.ActorId + "'.");
                }

                if (source.ChanceBasisPoints < 1 || source.ChanceBasisPoints > QuestContentRules.MaximumDropChanceBasisPoints)
                {
                    errors.Add(sourcePath + ".chanceBasisPoints must be between 1 and "
                        + QuestContentRules.MaximumDropChanceBasisPoints
                        + ".");
                }

                if (source.Quantity < 1 || source.Quantity > item.MaximumStackSize)
                {
                    errors.Add(sourcePath + ".quantity must be between 1 and " + item.MaximumStackSize + ", one stack.");
                }
            }
        }

        private static Dictionary<string, int> ValidateItemList(
            QuestItemQuantityAuthoringEntry[] items,
            int maximum,
            string path,
            List<string> errors,
            Action<QuestItemQuantityAuthoringEntry, string> validateItem)
        {
            var quantities = new Dictionary<string, int>(StringComparer.Ordinal);
            if (items == null)
            {
                errors.Add(path + " is required.");
                return quantities;
            }

            if (items.Length > maximum)
            {
                errors.Add(path + " may list at most " + maximum + " items.");
            }

            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var itemPath = path + "[" + Index(index) + "]";
                if (item == null)
                {
                    errors.Add(itemPath + " is required.");
                    continue;
                }

                validateItem(item, itemPath);
                if (item.DefinitionId == null)
                {
                    continue;
                }

                if (quantities.ContainsKey(item.DefinitionId))
                {
                    errors.Add(path + " contains duplicate id '" + item.DefinitionId + "'.");
                    continue;
                }

                quantities.Add(item.DefinitionId, item.Quantity);
            }

            return quantities;
        }

        private static void ValidateActorIds(
            string[] actorIds,
            string path,
            List<string> errors,
            Action<string, string> validateActor)
        {
            if (actorIds.Length > QuestContentRules.MaximumObjectiveTargets)
            {
                errors.Add(path + ".actorIds may name at most " + QuestContentRules.MaximumObjectiveTargets + " actors.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < actorIds.Length; index++)
            {
                validateActor(actorIds[index], path + ".actorIds[" + Index(index) + "]");
                if (actorIds[index] != null && !seen.Add(actorIds[index]))
                {
                    errors.Add(path + ".actorIds contains duplicate id '" + actorIds[index] + "'.");
                }
            }
        }

        private static void ValidateRequirementCycles(QuestAuthoringEntry[] quests, List<string> errors)
        {
            var required = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var quest in quests)
            {
                if (quest?.Id != null && !required.ContainsKey(quest.Id))
                {
                    required.Add(quest.Id, quest.RequiredQuestIds ?? Array.Empty<string>());
                }
            }

            // 1 while a quest's requirements are being visited, 2 once they are known to end.
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in required.Keys.OrderBy(id => id, StringComparer.Ordinal))
            {
                Visit(id, new List<string>());
            }

            void Visit(string id, List<string> trail)
            {
                if (!required.ContainsKey(id) || (state.TryGetValue(id, out var known) && known == 2))
                {
                    return;
                }

                trail.Add(id);
                if (known == 1)
                {
                    var cycle = trail.Skip(trail.IndexOf(id)).ToArray();
                    if (reported.Add(string.Join(",", cycle.OrderBy(value => value, StringComparer.Ordinal))))
                    {
                        errors.Add("requiredQuestIds form a cycle: " + string.Join(" -> ", cycle) + ".");
                    }

                    trail.RemoveAt(trail.Count - 1);
                    return;
                }

                state[id] = 1;
                foreach (var next in required[id])
                {
                    if (next != null)
                    {
                        Visit(next, trail);
                    }
                }

                state[id] = 2;
                trail.RemoveAt(trail.Count - 1);
            }
        }

        private static QuestDefinition CompileQuest(QuestAuthoringEntry quest)
        {
            var compiled = new QuestDefinition
            {
                Id = quest.Id,
                Title = quest.Title,
                GiverActorId = quest.GiverActorId,
                TurnInActorId = quest.TurnInActorId,
                MinimumLevel = quest.MinimumLevel,
                RequiredQuestIds = Sorted(quest.RequiredQuestIds),
                Repeatable = quest.Repeatable,
                Tags = Sorted(quest.Tags),
                OfferText = quest.OfferText,
                ProgressText = quest.ProgressText,
                CompletionText = quest.CompletionText,
                StartItems = CompileItems(quest.StartItems),
                Steps = quest.Steps.Select(step => new QuestStepDefinition
                {
                    Id = step.Id,
                    Objectives = step.Objectives.Select(CompileObjective).ToArray()
                }).ToArray(),
                RewardGold = quest.RewardGold,
                RewardItems = CompileItems(quest.RewardItems),
                RewardChoiceItems = CompileItems(quest.RewardChoiceItems)
            };
            compiled.StructuralFingerprint = StructuralFingerprintOf(compiled);
            return compiled;
        }

        private static QuestObjectiveDefinition CompileObjective(QuestObjectiveAuthoringEntry objective)
        {
            return new QuestObjectiveDefinition
            {
                Id = objective.Id,
                Kind = objective.Kind,
                Text = objective.Text,
                Count = objective.Count,
                ActorIds = Sorted(objective.ActorIds),
                ActorTag = objective.ActorTag ?? string.Empty,
                ItemId = objective.ItemId ?? string.Empty,
                NpcActorId = objective.NpcActorId ?? string.Empty,
                Line = objective.Line ?? string.Empty,
                DropSources = (objective.DropSources ?? Array.Empty<QuestDropSourceAuthoringEntry>())
                    .Select(source => new QuestDropSourceDefinition
                    {
                        ActorId = source.ActorId,
                        ChanceBasisPoints = source.ChanceBasisPoints,
                        Quantity = source.Quantity
                    })
                    .ToArray()
            };
        }

        private static QuestItemQuantityDefinition[] CompileItems(QuestItemQuantityAuthoringEntry[] items)
        {
            return (items ?? Array.Empty<QuestItemQuantityAuthoringEntry>())
                .Select(item => new QuestItemQuantityDefinition
                {
                    DefinitionId = item.DefinitionId,
                    Quantity = item.Quantity
                })
                .ToArray();
        }

        private static string StructuralFingerprintOf(QuestDefinition quest)
        {
            var values = new List<string> { "quest-structure", quest.Id };
            foreach (var step in quest.Steps ?? Array.Empty<QuestStepDefinition>())
            {
                values.Add("step:" + step.Id);
                foreach (var objective in step.Objectives ?? Array.Empty<QuestObjectiveDefinition>())
                {
                    values.Add("objective:" + objective.Id);
                    values.Add(objective.Kind);
                    values.Add(string.Join(",", objective.ActorIds ?? Array.Empty<string>()));
                    values.Add(objective.ActorTag);
                    values.Add(objective.ItemId);
                    values.Add(objective.NpcActorId);
                }
            }

            return Fingerprint(values);
        }

        private static string ComputeRevision(QuestCatalogRuntimeDocument runtime)
        {
            var values = new List<string>
            {
                "quest-catalog",
                runtime.FormatVersion.ToString(CultureInfo.InvariantCulture),
                runtime.CatalogId,
                runtime.MaximumActiveQuests.ToString(CultureInfo.InvariantCulture),
                string.Join(",", runtime.RetiredQuestIds)
            };
            foreach (var quest in runtime.Quests)
            {
                values.Add(quest.StructuralFingerprint);
                values.Add(quest.Title);
                values.Add(quest.GiverActorId);
                values.Add(quest.TurnInActorId);
                values.Add(quest.MinimumLevel.ToString(CultureInfo.InvariantCulture));
                values.Add(string.Join(",", quest.RequiredQuestIds));
                values.Add(quest.Repeatable ? "repeatable" : "once");
                values.Add(string.Join(",", quest.Tags));
                values.Add(quest.OfferText);
                values.Add(quest.ProgressText);
                values.Add(quest.CompletionText);
                values.Add(ItemsFingerprint(quest.StartItems));
                foreach (var objective in quest.Steps.SelectMany(step => step.Objectives))
                {
                    values.Add(objective.Text);
                    values.Add(objective.Count.ToString(CultureInfo.InvariantCulture));
                    values.Add(objective.Line);
                    values.Add(string.Join(",", objective.DropSources.Select(source =>
                        source.ActorId
                        + ":" + source.ChanceBasisPoints.ToString(CultureInfo.InvariantCulture)
                        + ":" + source.Quantity.ToString(CultureInfo.InvariantCulture))));
                }

                values.Add(quest.RewardGold.ToString(CultureInfo.InvariantCulture));
                values.Add(ItemsFingerprint(quest.RewardItems));
                values.Add(ItemsFingerprint(quest.RewardChoiceItems));
            }

            return Fingerprint(values);
        }

        private static string ItemsFingerprint(QuestItemQuantityDefinition[] items)
        {
            return string.Join(",", (items ?? Array.Empty<QuestItemQuantityDefinition>()).Select(item =>
                item.DefinitionId + ":" + item.Quantity.ToString(CultureInfo.InvariantCulture)));
        }

        private static string Fingerprint(IEnumerable<string> values)
        {
            return WorldActorCompiler.ComputeSha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", values)));
        }

        private static string[] Sorted(string[] values)
        {
            return (values ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static void ValidateText(string value, int maximumLength, string path, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(path + " is required.");
                return;
            }

            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.Length > maximumLength
                || value.Any(char.IsControl))
            {
                errors.Add(path + " must be one trimmed line of at most " + maximumLength + " characters.");
            }
        }

        private static void ValidateIdentifierSet(string[] values, int maximum, string path, List<string> errors)
        {
            if (values == null)
            {
                errors.Add(path + " is required.");
                return;
            }

            if (values.Length > maximum)
            {
                errors.Add(path + " may hold at most " + maximum + " entries.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (!IsIdentifier(value))
                {
                    errors.Add(path + " contains invalid identifier '" + value + "'.");
                }
                else if (!seen.Add(value))
                {
                    errors.Add(path + " contains duplicate id '" + value + "'.");
                }
            }
        }

        private static void RequireEmpty(string value, string path, List<string> errors)
        {
            if (!string.IsNullOrEmpty(value))
            {
                errors.Add(path + " must be empty for this objective kind.");
            }
        }

        private static void RequireNoActors(string[] actorIds, string actorTag, string path, List<string> errors)
        {
            if (actorIds.Length > 0)
            {
                errors.Add(path + ".actorIds must be empty for this objective kind.");
            }

            RequireEmpty(actorTag, path + ".actorTag", errors);
        }

        private static void RequireNoDropSources(QuestDropSourceAuthoringEntry[] dropSources, string path, List<string> errors)
        {
            if (dropSources.Length > 0)
            {
                errors.Add(path + ".dropSources must be empty for this objective kind.");
            }
        }

        private static bool IsIdentifier(string value)
        {
            return ContentLayout.IsValidDefinitionId(value);
        }

        private static string Path(int index)
        {
            return "quests[" + Index(index) + "]";
        }

        private static string Index(int index)
        {
            return index.ToString(CultureInfo.InvariantCulture);
        }

        // What quests may refer to: compiled items, the actors every World shares, and which of
        // them some World places.
        private sealed class QuestReferences
        {
            private readonly Dictionary<string, ItemDefinition> items;
            private readonly Dictionary<string, WorldActorDefinition> actors =
                new Dictionary<string, WorldActorDefinition>(StringComparer.Ordinal);
            private readonly HashSet<string> placedActorIds = new HashSet<string>(StringComparer.Ordinal);

            public QuestReferences(ItemCatalogRuntimeDocument items, IReadOnlyList<WorldActorRuntimeDocument> worlds)
            {
                this.items = (items.Definitions ?? Array.Empty<ItemDefinition>())
                    .ToDictionary(item => item.Id, StringComparer.Ordinal);
                MaximumCharacterLevel = items.Progression?.MaximumCharacterLevel ?? 1;
                foreach (var world in worlds)
                {
                    foreach (var actor in world.Actors ?? Array.Empty<WorldActorDefinition>())
                    {
                        if (!actors.ContainsKey(actor.Id))
                        {
                            actors.Add(actor.Id, actor);
                        }
                    }

                    foreach (var spawn in world.SpawnInstances ?? Array.Empty<WorldActorSpawnInstanceDefinition>())
                    {
                        placedActorIds.Add(spawn.ActorId);
                    }
                }
            }

            public int MaximumCharacterLevel { get; }

            public static bool IsQuestItem(ItemDefinition item)
            {
                return string.Equals(item.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal);
            }

            public static bool IsProtectedQuestItem(ItemDefinition item)
            {
                return IsQuestItem(item)
                    && (item.DefaultPolicies ?? Array.Empty<string>()).Contains(ItemPolicyIds.ProtectedOnDeath);
            }

            // False for a missing item, which the objective already reports.
            public bool IsQuestItem(string definitionId)
            {
                return !string.IsNullOrEmpty(definitionId)
                    && items.TryGetValue(definitionId, out var item)
                    && IsQuestItem(item);
            }

            // An NPC that takes part in quests has a quest capability and stands in some World.
            public void ValidateQuestNpc(string actorId, string path, List<string> errors)
            {
                if (!actors.TryGetValue(actorId ?? string.Empty, out var actor))
                {
                    errors.Add(path + " names missing id '" + actorId + "'.");
                    return;
                }

                if (!string.Equals(actor.Kind, WorldActorKindIds.Npc, StringComparison.Ordinal)
                    || !(actor.Capabilities ?? Array.Empty<WorldActorCapabilityDefinition>())
                        .Any(capability => QuestCapabilityKinds.Contains(capability.Kind)))
                {
                    errors.Add(path + " names '" + actorId + "', which is not an NPC with a quest capability.");
                    return;
                }

                RequirePlaced(actorId, path, errors);
            }

            public void ValidatePlacedActor(string actorId, string kind, string path, List<string> errors)
            {
                if (!actors.TryGetValue(actorId ?? string.Empty, out var actor))
                {
                    errors.Add(path + " names missing id '" + actorId + "'.");
                    return;
                }

                if (!string.Equals(actor.Kind, kind, StringComparison.Ordinal))
                {
                    errors.Add(path + " names '" + actorId + "', which is not a " + KindName(kind) + ".");
                    return;
                }

                RequirePlaced(actorId, path, errors);
            }

            public void ValidateMobTag(string tag, string path, List<string> errors)
            {
                if (!IsIdentifier(tag))
                {
                    errors.Add(path + " must be a content identifier.");
                    return;
                }

                if (!actors.Values.Any(actor =>
                        string.Equals(actor.Kind, WorldActorKindIds.Mob, StringComparison.Ordinal)
                        && placedActorIds.Contains(actor.Id)
                        && (actor.Tags ?? Array.Empty<string>()).Contains(tag)))
                {
                    errors.Add(path + " '" + tag + "' is carried by no Mob that a World places.");
                }
            }

            public ItemDefinition ValidateItem(string definitionId, string path, List<string> errors)
            {
                if (string.IsNullOrEmpty(definitionId) || !items.TryGetValue(definitionId, out var item))
                {
                    errors.Add(path + " names missing id '" + definitionId + "'.");
                    return null;
                }

                return item;
            }

            // A start item is a protected quest item that the quest's abandonment removes again.
            public void ValidateQuestItem(QuestItemQuantityAuthoringEntry entry, string path, List<string> errors)
            {
                var item = ValidateItem(entry.DefinitionId, path + ".definitionId", errors);
                if (item == null)
                {
                    return;
                }

                if (!IsProtectedQuestItem(item))
                {
                    errors.Add(path + ".definitionId '" + item.Id + "' must be a protected quest item.");
                }

                ValidateQuantity(entry, item, path, errors);
            }

            // A reward is an ordinary item that carried acquisition can place.
            public void ValidateRewardItem(QuestItemQuantityAuthoringEntry entry, string path, List<string> errors)
            {
                var item = ValidateItem(entry.DefinitionId, path + ".definitionId", errors);
                if (item == null)
                {
                    return;
                }

                if (IsQuestItem(item) || (item.DefaultPolicies ?? Array.Empty<string>()).Length > 0)
                {
                    errors.Add(path + ".definitionId '" + item.Id + "' cannot be a reward, because it is a quest item or has default policies.");
                }

                ValidateQuantity(entry, item, path, errors);
            }

            private static void ValidateQuantity(
                QuestItemQuantityAuthoringEntry entry,
                ItemDefinition item,
                string path,
                List<string> errors)
            {
                if (entry.Quantity < 1 || entry.Quantity > item.MaximumStackSize)
                {
                    errors.Add(path + ".quantity must be between 1 and " + item.MaximumStackSize + ", one stack.");
                }
            }

            private void RequirePlaced(string actorId, string path, List<string> errors)
            {
                if (!placedActorIds.Contains(actorId))
                {
                    errors.Add(path + " names '" + actorId + "', which no World places, so the quest could never be finished.");
                }
            }

            private static string KindName(string kind)
            {
                return string.Equals(kind, WorldActorKindIds.ResourceNode, StringComparison.Ordinal)
                    ? "resource node"
                    : kind == WorldActorKindIds.Mob ? "Mob" : kind;
            }
        }
    }
}
