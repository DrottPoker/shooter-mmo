using System;
using System.Collections.Generic;
using System.Linq;

namespace Mmorpg.WorldData.Editor.Quests
{
    // Applies the edits made on the base quest content to the content on disk now, quest by
    // quest: a quest that the edits left alone takes the disk's version, a quest that the disk
    // left alone keeps the edits, and a quest that both changed takes the disk's version and
    // counts as a conflict. Retired ids only grow, so the disk's stay and the edits add theirs,
    // except the id of a deleted quest that gave way to the disk's version.
    public static class QuestEditorMerge
    {
        public static QuestEditorCatalog Merge(
            QuestEditorCatalog baseCatalog,
            QuestEditorCatalog edited,
            QuestEditorCatalog disk,
            ICollection<string> conflicts)
        {
            if (baseCatalog == null || edited == null || disk == null)
            {
                throw new ArgumentNullException(baseCatalog == null ? nameof(baseCatalog) : edited == null ? nameof(edited) : nameof(disk));
            }

            if (conflicts == null)
            {
                throw new ArgumentNullException(nameof(conflicts));
            }

            var merged = QuestEditorJson.Clone(disk);
            if (edited.maximumActiveQuests != baseCatalog.maximumActiveQuests)
            {
                if (disk.maximumActiveQuests == baseCatalog.maximumActiveQuests
                    || disk.maximumActiveQuests == edited.maximumActiveQuests)
                {
                    merged.maximumActiveQuests = edited.maximumActiveQuests;
                }
                else
                {
                    conflicts.Add("the maximum of active quests");
                }
            }

            var baseQuests = ById(baseCatalog);
            var editedQuests = ById(edited);
            var diskQuests = ById(disk);
            var quests = new List<QuestEditorDefinition>();
            foreach (var id in baseQuests.Keys.Union(editedQuests.Keys).Union(diskQuests.Keys).OrderBy(id => id, StringComparer.Ordinal))
            {
                var original = baseQuests.TryGetValue(id, out var baseJson) ? baseJson : null;
                var mine = editedQuests.TryGetValue(id, out var editedJson) ? editedJson : null;
                var theirs = diskQuests.TryGetValue(id, out var diskJson) ? diskJson : null;
                string chosen;
                if (mine == original)
                {
                    chosen = theirs;
                }
                else if (theirs == original || theirs == mine)
                {
                    chosen = mine;
                }
                else
                {
                    chosen = theirs;
                    conflicts.Add(id);
                }

                if (chosen != null)
                {
                    quests.Add(QuestEditorJson.DeserializeDefinition(chosen));
                }
            }

            merged.quests = quests.ToArray();
            var kept = new HashSet<string>(quests.Select(quest => quest.id), StringComparer.Ordinal);
            var baseRetired = baseCatalog.retiredQuestIds ?? Array.Empty<string>();
            merged.retiredQuestIds = (disk.retiredQuestIds ?? Array.Empty<string>())
                .Concat((edited.retiredQuestIds ?? Array.Empty<string>())
                    .Where(id => !baseRetired.Contains(id, StringComparer.Ordinal) && !kept.Contains(id)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return merged;
        }

        private static Dictionary<string, string> ById(QuestEditorCatalog catalog)
        {
            var quests = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var quest in catalog.quests ?? Array.Empty<QuestEditorDefinition>())
            {
                if (quest?.id != null && !quests.ContainsKey(quest.id))
                {
                    quests.Add(quest.id, QuestEditorJson.Serialize(quest));
                }
            }

            return quests;
        }
    }
}
