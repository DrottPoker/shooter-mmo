using System;
using System.Linq;

namespace Mmorpg.WorldData.Quests
{
    // The camelCase field shape of the compiled quest catalog for Unity's JsonUtility, which reads
    // fields by their exact names. The Unity client reads the bundled catalog with it and checks
    // the result with QuestCatalogCompiler.ValidateRuntime, so a new runtime field is added here
    // too.
    [Serializable]
    public sealed class QuestCatalogJsonDocument
    {
        public int formatVersion;
        public string catalogId;
        public string revision;
        public int maximumActiveQuests;
        public string[] retiredQuestIds;
        public QuestJsonDefinition[] quests;

        public QuestCatalogRuntimeDocument ToRuntime()
        {
            return new QuestCatalogRuntimeDocument
            {
                FormatVersion = formatVersion,
                CatalogId = catalogId,
                Revision = revision,
                MaximumActiveQuests = maximumActiveQuests,
                RetiredQuestIds = retiredQuestIds ?? Array.Empty<string>(),
                Quests = (quests ?? Array.Empty<QuestJsonDefinition>())
                    .Select(quest => quest?.ToRuntime())
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class QuestJsonDefinition
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
        public QuestJsonItemQuantity[] startItems;
        public QuestJsonStep[] steps;
        public long rewardGold;
        public QuestJsonItemQuantity[] rewardItems;
        public QuestJsonItemQuantity[] rewardChoiceItems;
        public string structuralFingerprint;

        public QuestDefinition ToRuntime()
        {
            return new QuestDefinition
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
                StartItems = QuestJsonItemQuantity.ToRuntime(startItems),
                Steps = (steps ?? Array.Empty<QuestJsonStep>()).Select(step => step?.ToRuntime()).ToArray(),
                RewardGold = rewardGold,
                RewardItems = QuestJsonItemQuantity.ToRuntime(rewardItems),
                RewardChoiceItems = QuestJsonItemQuantity.ToRuntime(rewardChoiceItems),
                StructuralFingerprint = structuralFingerprint
            };
        }
    }

    [Serializable]
    public sealed class QuestJsonStep
    {
        public string id;
        public QuestJsonObjective[] objectives;

        public QuestStepDefinition ToRuntime()
        {
            return new QuestStepDefinition
            {
                Id = id,
                Objectives = (objectives ?? Array.Empty<QuestJsonObjective>())
                    .Select(objective => objective?.ToRuntime())
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class QuestJsonObjective
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
        public QuestJsonDropSource[] dropSources;

        public QuestObjectiveDefinition ToRuntime()
        {
            return new QuestObjectiveDefinition
            {
                Id = id,
                Kind = kind,
                Text = text,
                Count = count,
                ActorIds = actorIds ?? Array.Empty<string>(),
                ActorTag = actorTag,
                ItemId = itemId,
                NpcActorId = npcActorId,
                Line = line,
                DropSources = (dropSources ?? Array.Empty<QuestJsonDropSource>())
                    .Select(source => source == null
                        ? null
                        : new QuestDropSourceDefinition
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
    public sealed class QuestJsonDropSource
    {
        public string actorId;
        public int chanceBasisPoints;
        public int quantity;
    }

    [Serializable]
    public sealed class QuestJsonItemQuantity
    {
        public string definitionId;
        public int quantity;

        public static QuestItemQuantityDefinition[] ToRuntime(QuestJsonItemQuantity[] values)
        {
            return (values ?? Array.Empty<QuestJsonItemQuantity>())
                .Select(value => value == null
                    ? null
                    : new QuestItemQuantityDefinition
                    {
                        DefinitionId = value.definitionId,
                        Quantity = value.quantity
                    })
                .ToArray();
        }
    }
}
