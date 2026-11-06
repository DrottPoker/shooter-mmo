using System;
using System.Collections.Generic;
using System.Linq;

namespace Mmorpg.WorldData.Editor.Quests
{
    // The quests an NPC gives, receives, and appears in as an objective's NPC.
    public sealed class QuestEditorNpcQuests
    {
        public QuestEditorNpcQuests(
            IReadOnlyList<QuestEditorDefinition> gives,
            IReadOnlyList<QuestEditorDefinition> receives,
            IReadOnlyList<QuestEditorDefinition> appearsIn)
        {
            Gives = gives ?? Array.Empty<QuestEditorDefinition>();
            Receives = receives ?? Array.Empty<QuestEditorDefinition>();
            AppearsIn = appearsIn ?? Array.Empty<QuestEditorDefinition>();
        }

        public IReadOnlyList<QuestEditorDefinition> Gives { get; }
        public IReadOnlyList<QuestEditorDefinition> Receives { get; }
        public IReadOnlyList<QuestEditorDefinition> AppearsIn { get; }
    }

    // How quests relate, for Quest Studio's chain and NPC views.
    public static class QuestEditorRelations
    {
        // The chain of the quest: every quest linked to it through required quests, in layers.
        // A quest sits one layer below the deepest quest it requires, so each layer needs only
        // layers above it. A cycle, which the compiler rejects, is cut where it repeats.
        public static IReadOnlyList<IReadOnlyList<QuestEditorDefinition>> Chain(QuestEditorCatalog catalog, string questId)
        {
            var quests = ById(catalog);
            if (questId == null || !quests.ContainsKey(questId))
            {
                return Array.Empty<IReadOnlyList<QuestEditorDefinition>>();
            }

            var followUps = quests.Values
                .SelectMany(quest => Requirements(quest, quests).Select(required => (Required: required, FollowUp: quest.id)))
                .ToLookup(link => link.Required, link => link.FollowUp, StringComparer.Ordinal);
            var members = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(questId);
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                if (!members.Add(id))
                {
                    continue;
                }

                foreach (var linked in Requirements(quests[id], quests).Concat(followUps[id]))
                {
                    pending.Push(linked);
                }
            }

            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            int Depth(string id)
            {
                if (depths.TryGetValue(id, out var known))
                {
                    return known;
                }

                if (!visiting.Add(id))
                {
                    return 0;
                }

                var depth = Requirements(quests[id], quests).Select(required => Depth(required) + 1).DefaultIfEmpty(0).Max();
                visiting.Remove(id);
                depths[id] = depth;
                return depth;
            }

            return members
                .GroupBy(Depth)
                .OrderBy(layer => layer.Key)
                .Select(layer => (IReadOnlyList<QuestEditorDefinition>)layer
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .Select(id => quests[id])
                    .ToArray())
                .ToArray();
        }

        public static QuestEditorNpcQuests ForNpc(QuestEditorCatalog catalog, string actorId)
        {
            var quests = (catalog?.quests ?? Array.Empty<QuestEditorDefinition>())
                .Where(quest => quest != null && !string.IsNullOrEmpty(actorId))
                .ToArray();
            return new QuestEditorNpcQuests(
                quests.Where(quest => quest.giverActorId == actorId).ToArray(),
                quests.Where(quest => quest.turnInActorId == actorId).ToArray(),
                quests.Where(quest => quest.Objectives().Any(objective => objective.npcActorId == actorId)).ToArray());
        }

        // The quests that require the quest directly.
        public static IReadOnlyList<QuestEditorDefinition> FollowUps(QuestEditorCatalog catalog, string questId)
        {
            return (catalog?.quests ?? Array.Empty<QuestEditorDefinition>())
                .Where(quest => quest != null && (quest.requiredQuestIds ?? Array.Empty<string>()).Contains(questId, StringComparer.Ordinal))
                .ToArray();
        }

        private static Dictionary<string, QuestEditorDefinition> ById(QuestEditorCatalog catalog)
        {
            var quests = new Dictionary<string, QuestEditorDefinition>(StringComparer.Ordinal);
            foreach (var quest in catalog?.quests ?? Array.Empty<QuestEditorDefinition>())
            {
                if (!string.IsNullOrEmpty(quest?.id) && !quests.ContainsKey(quest.id))
                {
                    quests.Add(quest.id, quest);
                }
            }

            return quests;
        }

        // The required quests that exist, without the quest itself.
        private static IEnumerable<string> Requirements(QuestEditorDefinition quest, IReadOnlyDictionary<string, QuestEditorDefinition> quests)
        {
            return (quest.requiredQuestIds ?? Array.Empty<string>())
                .Where(id => id != null && id != quest.id && quests.ContainsKey(id))
                .Distinct(StringComparer.Ordinal);
        }
    }
}
