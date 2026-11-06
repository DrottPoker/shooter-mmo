using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Quests;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Quests
{
    // The editable quest content: catalog.json and one file per quest definition. The fields
    // mirror the authoring files in their order, so JsonUtility writes them byte for byte as
    // they are committed.
    [Serializable]
    public sealed class QuestEditorCatalog
    {
        public int formatVersion;
        public string catalogId;
        public int maximumActiveQuests;
        public string[] retiredQuestIds;
        public QuestEditorDefinition[] quests;

        public QuestCatalogAuthoringDocument ToDomain()
        {
            return new QuestCatalogAuthoringDocument
            {
                FormatVersion = formatVersion,
                CatalogId = catalogId,
                MaximumActiveQuests = maximumActiveQuests,
                RetiredQuestIds = retiredQuestIds ?? Array.Empty<string>(),
                Quests = (quests ?? Array.Empty<QuestEditorDefinition>()).Select(quest => quest.ToDomain()).ToArray()
            };
        }
    }

    [Serializable]
    public sealed class QuestEditorCatalogHeader
    {
        public int formatVersion;
        public string catalogId;
        public int maximumActiveQuests;
        public string[] retiredQuestIds;
    }

    [Serializable]
    public sealed class QuestEditorDefinition
    {
        public string id;
        public string title;
        public string giverActorId;
        public string turnInActorId;
        public int minimumLevel;
        public string[] requiredQuestIds;
        public bool repeatable;
        public string[] tags;
        public string offerText;
        public string progressText;
        public string completionText;
        public QuestEditorItemQuantity[] startItems;
        public QuestEditorStep[] steps;
        public long rewardGold;
        public QuestEditorItemQuantity[] rewardItems;
        public QuestEditorItemQuantity[] rewardChoiceItems;

        public QuestAuthoringEntry ToDomain()
        {
            return new QuestAuthoringEntry
            {
                Id = id,
                Title = title,
                GiverActorId = giverActorId,
                TurnInActorId = turnInActorId,
                MinimumLevel = minimumLevel,
                RequiredQuestIds = requiredQuestIds ?? Array.Empty<string>(),
                Repeatable = repeatable,
                Tags = tags ?? Array.Empty<string>(),
                OfferText = offerText,
                ProgressText = progressText,
                CompletionText = completionText,
                StartItems = QuestEditorItemQuantity.ToDomain(startItems),
                Steps = (steps ?? Array.Empty<QuestEditorStep>()).Select(step => step.ToDomain()).ToArray(),
                RewardGold = rewardGold,
                RewardItems = QuestEditorItemQuantity.ToDomain(rewardItems),
                RewardChoiceItems = QuestEditorItemQuantity.ToDomain(rewardChoiceItems)
            };
        }

        // Every NPC the quest names: its giver, its turn-in NPC, and the NPCs of its objectives.
        public IEnumerable<string> NpcActorIds()
        {
            yield return giverActorId ?? string.Empty;
            yield return turnInActorId ?? string.Empty;
            foreach (var objective in Objectives())
            {
                if (!string.IsNullOrEmpty(objective.npcActorId))
                {
                    yield return objective.npcActorId;
                }
            }
        }

        public IEnumerable<QuestEditorObjective> Objectives()
        {
            return (steps ?? Array.Empty<QuestEditorStep>())
                .SelectMany(step => step?.objectives ?? Array.Empty<QuestEditorObjective>())
                .Where(objective => objective != null);
        }
    }

    [Serializable]
    public sealed class QuestEditorStep
    {
        public string id;
        public QuestEditorObjective[] objectives;

        public QuestStepAuthoringEntry ToDomain()
        {
            return new QuestStepAuthoringEntry
            {
                Id = id,
                Objectives = (objectives ?? Array.Empty<QuestEditorObjective>())
                    .Select(objective => objective.ToDomain())
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class QuestEditorObjective
    {
        public string id;
        public string kind;
        public string text;
        public int count;
        public string[] actorIds;
        public string actorTag;
        public string itemId;
        public string npcActorId;
        public string line;
        public QuestEditorDropSource[] dropSources;

        public QuestObjectiveAuthoringEntry ToDomain()
        {
            return new QuestObjectiveAuthoringEntry
            {
                Id = id,
                Kind = kind,
                Text = text,
                Count = count,
                ActorIds = actorIds ?? Array.Empty<string>(),
                ActorTag = actorTag ?? string.Empty,
                ItemId = itemId ?? string.Empty,
                NpcActorId = npcActorId ?? string.Empty,
                Line = line ?? string.Empty,
                DropSources = (dropSources ?? Array.Empty<QuestEditorDropSource>())
                    .Select(source => new QuestDropSourceAuthoringEntry
                    {
                        ActorId = source.actorId,
                        ChanceBasisPoints = source.chanceBasisPoints,
                        Quantity = source.quantity
                    })
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class QuestEditorDropSource
    {
        public string actorId;
        public int chanceBasisPoints;
        public int quantity;
    }

    [Serializable]
    public sealed class QuestEditorItemQuantity
    {
        public string definitionId;
        public int quantity;

        public static QuestItemQuantityAuthoringEntry[] ToDomain(QuestEditorItemQuantity[] items)
        {
            return (items ?? Array.Empty<QuestEditorItemQuantity>())
                .Select(item => new QuestItemQuantityAuthoringEntry
                {
                    DefinitionId = item.definitionId,
                    Quantity = item.quantity
                })
                .ToArray();
        }
    }

    public static class QuestEditorJson
    {
        public static QuestEditorCatalogHeader DeserializeCatalogHeader(string json)
        {
            return Deserialize<QuestEditorCatalogHeader>(json, "quest catalog");
        }

        public static QuestEditorDefinition DeserializeDefinition(string json)
        {
            return Deserialize<QuestEditorDefinition>(json, "quest definition");
        }

        public static string Serialize<T>(T document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            return JsonUtility.ToJson(document, true).Replace("\r\n", "\n") + "\n";
        }

        public static T Clone<T>(T document)
        {
            return Deserialize<T>(Serialize(document), typeof(T).Name);
        }

        private static T Deserialize<T>(string json, string label)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("The " + label + " JSON is empty.");
            }

            try
            {
                var value = JsonUtility.FromJson<T>(json);
                if (value == null)
                {
                    throw new InvalidDataException("The " + label + " JSON has no root object.");
                }

                return value;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The " + label + " JSON is malformed: " + exception.Message, exception);
            }
        }
    }

    // Reads and writes content/quests: catalog.json plus one file per quest definition.
    public static class QuestEditorContent
    {
        public static QuestEditorCatalog Load(string questsDirectory)
        {
            var catalogPath = ContentLayout.CatalogPath(questsDirectory);
            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("The quest catalog file is missing.", catalogPath);
            }

            var header = QuestEditorJson.DeserializeCatalogHeader(File.ReadAllText(catalogPath));
            return new QuestEditorCatalog
            {
                formatVersion = header.formatVersion,
                catalogId = header.catalogId,
                maximumActiveQuests = header.maximumActiveQuests,
                retiredQuestIds = header.retiredQuestIds ?? Array.Empty<string>(),
                quests = ContentLayout.ListDefinitionFiles(questsDirectory).Select(LoadDefinition).ToArray()
            };
        }

        // File text keyed by path relative to the quests directory. Validation reports a
        // duplicate id before this runs.
        public static Dictionary<string, string> CreateFiles(QuestEditorCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContentLayout.CatalogFileName] = QuestEditorJson.Serialize(new QuestEditorCatalogHeader
                {
                    formatVersion = catalog.formatVersion,
                    catalogId = catalog.catalogId,
                    maximumActiveQuests = catalog.maximumActiveQuests,
                    retiredQuestIds = catalog.retiredQuestIds ?? Array.Empty<string>()
                })
            };
            foreach (var quest in catalog.quests ?? Array.Empty<QuestEditorDefinition>())
            {
                var path = Path.Combine(ContentLayout.DefinitionsDirectoryName, ContentLayout.DefinitionFileName(quest.id));
                if (files.ContainsKey(path))
                {
                    throw new InvalidDataException("Quest id '" + quest.id + "' is used more than once.");
                }

                files.Add(path, QuestEditorJson.Serialize(quest));
            }

            return files;
        }

        private static QuestEditorDefinition LoadDefinition(ContentDefinitionFile file)
        {
            QuestEditorDefinition quest;
            try
            {
                quest = QuestEditorJson.DeserializeDefinition(File.ReadAllText(file.Path));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(file.Path + ": " + exception.Message, exception);
            }

            if (!string.Equals(quest.id, file.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Quest definition file '" + file.Path + "' must contain id '" + file.Id + "'.");
            }

            return quest;
        }
    }
}
