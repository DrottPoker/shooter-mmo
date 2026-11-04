using System;
using System.Collections.Generic;

namespace Mmorpg.WorldData.Quests
{
    public static class QuestCatalogFormat
    {
        public const int Version = 1;
        public const string QuestsDirectoryName = "quests";
        public const string CatalogFileName = "core.quest-catalog.json";
    }

    // The limits keep a quest readable in the quest dialog, the log, and the tracker, and every
    // quest message small enough for one realtime packet.
    public static class QuestContentRules
    {
        public const int MaximumIdentifierLength = 128;
        public const int MaximumTitleLength = 64;
        public const int MaximumOfferTextLength = 1000;
        public const int MaximumProgressTextLength = 500;
        public const int MaximumCompletionTextLength = 1000;
        public const int MaximumObjectiveTextLength = 64;
        public const int MaximumLineLength = 500;
        public const int MaximumSteps = 8;
        public const int MaximumObjectivesPerStep = 8;
        public const int MaximumObjectiveCount = 1000;
        public const int MaximumStartItems = 4;
        public const int MaximumRewardItems = 4;
        public const int MinimumRewardChoices = 2;
        public const int MaximumRewardChoices = 6;
        public const long MaximumRewardGold = 1_000_000L;
        public const int MaximumRequiredQuests = 8;
        public const int MaximumTags = 16;
        public const int MaximumObjectiveTargets = 8;
        public const int MaximumDropSources = 8;
        public const int MaximumDropChanceBasisPoints = 10_000;
        public const int MinimumActiveQuestLimit = 1;
        public const int MaximumActiveQuestLimit = 100;
    }

    // What an objective asks for. Each kind uses its own fields and leaves the others empty.
    public static class QuestObjectiveKindIds
    {
        // Credited kills of Mobs of the listed actor definitions or with one actor tag.
        public const string Kill = "kill";

        // A quantity of an item in carried storage, taken at the turn-in.
        public const string Collect = "collect";

        // A quantity of an item handed to an NPC on the way.
        public const string Deliver = "deliver";

        // A conversation with an NPC.
        public const string TalkTo = "talk_to";

        // Items gathered from resource nodes of the listed actor definitions.
        public const string Gather = "gather";
    }

    [Serializable]
    public sealed class QuestCatalogAuthoringDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public int MaximumActiveQuests;
        public string[] RetiredQuestIds;
        public QuestAuthoringEntry[] Quests;
    }

    [Serializable]
    public sealed class QuestAuthoringEntry
    {
        public string Id;
        public string Title;
        public string GiverActorId;
        public string TurnInActorId;
        public int MinimumLevel;
        public string[] RequiredQuestIds;
        public bool Repeatable;
        public string[] Tags;
        public string OfferText;
        public string ProgressText;
        public string CompletionText;
        public QuestItemQuantityAuthoringEntry[] StartItems;
        public QuestStepAuthoringEntry[] Steps;
        public long RewardGold;
        public QuestItemQuantityAuthoringEntry[] RewardItems;
        public QuestItemQuantityAuthoringEntry[] RewardChoiceItems;
    }

    [Serializable]
    public sealed class QuestStepAuthoringEntry
    {
        public string Id;
        public QuestObjectiveAuthoringEntry[] Objectives;
    }

    [Serializable]
    public sealed class QuestObjectiveAuthoringEntry
    {
        public string Id;
        public string Kind;
        public string Text;
        public int Count;

        // The Mobs of a kill objective, or the resource nodes of a gather objective.
        public string[] ActorIds;

        // The one actor tag of a kill objective that names no actors.
        public string ActorTag;

        // The item of a collect or deliver objective.
        public string ItemId;

        // The NPC of a deliver or talk-to objective.
        public string NpcActorId;

        // What the NPC of a talk-to objective says.
        public string Line;

        // Where the quest item of a collect objective drops for the players on the quest.
        public QuestDropSourceAuthoringEntry[] DropSources;
    }

    [Serializable]
    public sealed class QuestDropSourceAuthoringEntry
    {
        public string ActorId;
        public int ChanceBasisPoints;
        public int Quantity;
    }

    [Serializable]
    public sealed class QuestItemQuantityAuthoringEntry
    {
        public string DefinitionId;
        public int Quantity;
    }

    [Serializable]
    public sealed class QuestCatalogRuntimeDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public string Revision;
        public int MaximumActiveQuests;
        public string[] RetiredQuestIds;
        public QuestDefinition[] Quests;
    }

    [Serializable]
    public sealed class QuestDefinition
    {
        public string Id;
        public string Title;
        public string GiverActorId;
        public string TurnInActorId;
        public int MinimumLevel;
        public string[] RequiredQuestIds;
        public bool Repeatable;
        public string[] Tags;
        public string OfferText;
        public string ProgressText;
        public string CompletionText;
        public QuestItemQuantityDefinition[] StartItems;
        public QuestStepDefinition[] Steps;
        public long RewardGold;
        public QuestItemQuantityDefinition[] RewardItems;
        public QuestItemQuantityDefinition[] RewardChoiceItems;

        // Covers the steps and the ids, kinds, and targets of their objectives, which stored
        // progress depends on; texts, counts, and rewards may change without changing it.
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class QuestStepDefinition
    {
        public string Id;
        public QuestObjectiveDefinition[] Objectives;
    }

    [Serializable]
    public sealed class QuestObjectiveDefinition
    {
        public string Id;
        public string Kind;
        public string Text;
        public int Count;
        public string[] ActorIds;
        public string ActorTag;
        public string ItemId;
        public string NpcActorId;
        public string Line;
        public QuestDropSourceDefinition[] DropSources;
    }

    [Serializable]
    public sealed class QuestDropSourceDefinition
    {
        public string ActorId;
        public int ChanceBasisPoints;
        public int Quantity;
    }

    [Serializable]
    public sealed class QuestItemQuantityDefinition
    {
        public string DefinitionId;
        public int Quantity;
    }

    public sealed class QuestValidationException : Exception
    {
        public QuestValidationException(IReadOnlyList<string> errors)
            : base("Quest content is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<string> Errors { get; }
    }
}
