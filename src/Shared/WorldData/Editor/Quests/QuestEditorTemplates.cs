using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.WorldData.Editor.Quests
{
    // The quests that New starts from. Each compiles once the World has two quest NPCs, a Mob,
    // a resource node, and an ordinary material; the author then changes what fits the story. A
    // quest item belongs to one quest, so no template takes one that another quest uses.
    public static class QuestEditorTemplates
    {
        public const string Kill = "kill";
        public const string Collect = "collect";
        public const string Delivery = "delivery";
        public const string TalkTo = "talk_to";
        public const string Gather = "gather";
        public const string MultiStep = "multi_step";

        public static readonly IReadOnlyList<string> All = new[] { Kill, Collect, Delivery, TalkTo, Gather, MultiStep };

        // The item category a collect quest starts with, such as wolf pelts.
        private const string MaterialCategory = "material";

        public static string Label(string template)
        {
            switch (template)
            {
                case Kill:
                    return "Kill Quest";
                case Collect:
                    return "Collect Quest";
                case Delivery:
                    return "Delivery";
                case TalkTo:
                    return "Talk-To Quest";
                case Gather:
                    return "Gathering Quest";
                case MultiStep:
                    return "Several Steps";
                default:
                    throw new ArgumentOutOfRangeException(nameof(template), template, null);
            }
        }

        public static QuestEditorDefinition Create(string template, string id, QuestEditorReferences references)
        {
            if (references == null)
            {
                throw new ArgumentNullException(nameof(references));
            }

            var npcs = references.QuestNpcs.Select(actor => actor.Id).ToArray();
            var giver = npcs.FirstOrDefault() ?? string.Empty;
            var other = npcs.Skip(1).FirstOrDefault() ?? giver;
            var mob = references.Mobs.Select(actor => actor.Id).FirstOrDefault() ?? string.Empty;
            var node = references.ResourceNodes.Select(actor => actor.Id).FirstOrDefault() ?? string.Empty;
            var material = references.ItemChoices
                .FirstOrDefault(item => item.Category == MaterialCategory)?.Id ?? string.Empty;
            var quest = new QuestEditorDefinition
            {
                id = id,
                title = "New " + Label(template),
                giverActorId = giver,
                turnInActorId = giver,
                minimumLevel = 1,
                requiredQuestIds = Array.Empty<string>(),
                repeatable = false,
                tags = Array.Empty<string>(),
                offerText = "Will you help me?",
                progressText = "How is it going?",
                completionText = "Thank you.",
                startItems = Array.Empty<QuestEditorItemQuantity>(),
                rewardGold = 10,
                rewardItems = Array.Empty<QuestEditorItemQuantity>(),
                rewardChoiceItems = Array.Empty<QuestEditorItemQuantity>()
            };
            switch (template)
            {
                case Kill:
                    quest.steps = new[] { Step("hunt", KillObjective("kills", mob)) };
                    break;
                case Collect:
                    quest.steps = new[] { Step("collect", CollectObjective("items", material)) };
                    break;
                case Delivery:
                    quest.steps = new[]
                    {
                        Step("deliver", Objective("delivery", QuestObjectiveKindIds.Deliver, "Items handed over", 3, itemId: material, npcActorId: other))
                    };
                    break;
                case TalkTo:
                    quest.steps = new[]
                    {
                        Step("talk", Objective("talk", QuestObjectiveKindIds.TalkTo, "Talked to", 1, npcActorId: other, line: "So you came."))
                    };
                    break;
                case Gather:
                    quest.steps = new[]
                    {
                        Step("gather", Objective("yields", QuestObjectiveKindIds.Gather, "Gathered", 5, actorIds: new[] { node }))
                    };
                    break;
                case MultiStep:
                    quest.steps = new[]
                    {
                        Step("hunt", KillObjective("kills", mob)),
                        Step("report", Objective("report", QuestObjectiveKindIds.TalkTo, "Reported", 1, npcActorId: other, line: "Good work."))
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(template), template, null);
            }

            return quest;
        }

        // A fresh objective of a kind, as Add Objective makes it.
        public static QuestEditorObjective Objective(
            string id,
            string kind,
            string text,
            int count,
            string[] actorIds = null,
            string itemId = "",
            string npcActorId = "",
            string line = "")
        {
            return new QuestEditorObjective
            {
                id = id,
                kind = kind,
                text = text,
                count = count,
                actorIds = actorIds ?? Array.Empty<string>(),
                actorTag = string.Empty,
                itemId = itemId,
                npcActorId = npcActorId,
                line = line,
                dropSources = Array.Empty<QuestEditorDropSource>()
            };
        }

        public static QuestEditorStep Step(string id, params QuestEditorObjective[] objectives)
        {
            return new QuestEditorStep { id = id, objectives = objectives };
        }

        // The first id from the base that neither the catalog nor its retired ids use, nor any id
        // that reserved holds, such as a saved quest's that undo took out of the catalog.
        public static string UniqueId(string baseId, QuestEditorCatalog catalog, Func<string, bool> reserved = null)
        {
            var used = new HashSet<string>(
                (catalog?.quests ?? Array.Empty<QuestEditorDefinition>()).Select(quest => quest?.id)
                    .Concat(catalog?.retiredQuestIds ?? Array.Empty<string>())
                    .Where(id => id != null),
                StringComparer.Ordinal);
            bool Taken(string id)
            {
                return used.Contains(id) || (reserved != null && reserved(id));
            }

            if (!Taken(baseId))
            {
                return baseId;
            }

            for (var suffix = 2; ; suffix++)
            {
                var candidate = baseId + "_" + suffix;
                if (!Taken(candidate))
                {
                    return candidate;
                }
            }
        }

        private static QuestEditorObjective KillObjective(string id, string mob)
        {
            return Objective(id, QuestObjectiveKindIds.Kill, "Slain", 5, actorIds: new[] { mob });
        }

        private static QuestEditorObjective CollectObjective(string id, string item)
        {
            return Objective(id, QuestObjectiveKindIds.Collect, "Collected", 3, itemId: item);
        }
    }
}
