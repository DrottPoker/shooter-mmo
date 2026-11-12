namespace Mmorpg.GameService.Quests;

// The error codes of the quest commands. Their values are client and worker protocol; the
// codes that the quest commands share with the item commands are Items' codes.
internal static class QuestTransactionErrorCodes
{
    public const string QuestNotFound = "quest_not_found";
    public const string QuestNotActive = "quest_not_active";
    public const string QuestAlreadyActive = "quest_already_active";
    public const string QuestAlreadyCompleted = "quest_already_completed";
    public const string QuestRequirementsNotMet = "quest_requirements_not_met";
    public const string QuestLevelTooLow = "quest_level_too_low";
    public const string QuestLogFull = "quest_log_full";
    public const string QuestNpcAccessRequired = "quest_npc_access_required";
    public const string QuestObjectiveInvalid = "quest_objective_invalid";
    public const string QuestObjectiveComplete = "quest_objective_complete";
    public const string QuestItemsMissing = "quest_items_missing";
    public const string QuestNotReady = "quest_not_ready";
    public const string QuestRewardChoiceRequired = "quest_reward_choice_required";
    public const string QuestRewardChoiceInvalid = "quest_reward_choice_invalid";
    public const string DevelopmentToolsDisabled = "development_tools_disabled";
}
