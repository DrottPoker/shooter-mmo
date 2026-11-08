using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.WorldData.Editor.Quests
{
    // Live validation of quest content with the shared QuestCatalogCompiler. Every finding is
    // keyed by its quest id, with the path inside the quest, such as
    // "steps[0].objectives[1].count"; a finding about the catalog settings has no key and names
    // the setting as its field, such as "maximumActiveQuests".
    public static class QuestEditorIssues
    {
        public const string Quests = "quests";

        public const string MaximumActiveQuests = "maximumActiveQuests";

        public static IReadOnlyList<ContentIssue> Collect(QuestEditorCatalog catalog, QuestEditorReferences references)
        {
            if (catalog == null || references == null)
            {
                return Array.Empty<ContentIssue>();
            }

            try
            {
                QuestCatalogCompiler.Compile(catalog.ToDomain(), references.Items, references.Worlds);
                return Array.Empty<ContentIssue>();
            }
            catch (QuestValidationException exception)
            {
                var ids = (catalog.quests ?? Array.Empty<QuestEditorDefinition>()).Select(quest => quest?.id).ToArray();
                return exception.Errors
                    .Select(error => ContentIssuePath.ToIssue(error, Quests, index => index < ids.Length ? ids[index] : null))
                    .Select(issue => issue.EntityKey.Length == 0
                        && issue.Field.Length == 0
                        && issue.Message.StartsWith(MaximumActiveQuests + " ", StringComparison.Ordinal)
                            ? new ContentIssue(string.Empty, MaximumActiveQuests, issue.Message)
                            : issue)
                    .ToArray();
            }
            catch (Exception exception)
            {
                return new[] { new ContentIssue(string.Empty, string.Empty, exception.Message) };
            }
        }

        // The finding with the quest it concerns, such as "Wolves at the Gate
        // (quest.wolves_at_the_gate): title must be one trimmed line ...". The message already
        // starts with the field path.
        public static string Describe(ContentIssue issue, QuestEditorCatalog catalog)
        {
            if (string.IsNullOrEmpty(issue.EntityKey))
            {
                return issue.Message;
            }

            var quest = catalog?.quests?.FirstOrDefault(entry => string.Equals(entry?.id, issue.EntityKey, StringComparison.Ordinal));
            var name = quest == null || string.IsNullOrEmpty(quest.title)
                ? issue.EntityKey
                : quest.title + " (" + issue.EntityKey + ")";
            return name + ": " + issue.Message;
        }
    }
}
