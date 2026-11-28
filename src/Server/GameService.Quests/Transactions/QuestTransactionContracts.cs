using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Quests;

// The quest commands with their operation kinds, the quest log and progression changes that
// their results carry, and the quest NPC capability that the worker claims for them.

// Removes every protected item of an abandoned quest grant.
internal sealed record AbandonQuestItemsCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string QuestGrantId);

// Accepts an available quest at its giver and grants its start items with the quest's
// lineage into carried storage.
internal sealed record AcceptQuestCommand(
    Guid CharacterId,
    string QuestId);

// Ends an active quest without completing it and removes every item with its lineage,
// wherever the character owns it.
internal sealed record AbandonQuestCommand(
    Guid CharacterId,
    string QuestId);

// Grants an active quest's start items again, as far as the character no longer owns them
// and the quest still needs them.
internal sealed record RegrantQuestItemsCommand(
    Guid CharacterId,
    string QuestId);

// Completes a talk-to objective of the current step at its NPC.
internal sealed record TalkToQuestNpcCommand(
    Guid CharacterId,
    string QuestId,
    string ObjectiveId);

// Hands over the items of a deliver objective of the current step at its NPC.
internal sealed record DeliverQuestItemsCommand(
    Guid CharacterId,
    string QuestId,
    string ObjectiveId);

// Credits one kill of a Mob to a character: the experience for it, and one count for every
// matching kill objective of the character's current quest steps.
internal sealed record CreditMobKillCommand(
    Guid CharacterId,
    string MobActorId,
    IReadOnlyList<string> MobTags,
    int MobLevel,
    int MobExperiencePercent);

// Completes a ready quest at its turn-in NPC: takes the collected items, removes what is left
// with the quest's lineage, and grants the rewards with the chosen one.
internal sealed record TurnInQuestCommand(
    Guid CharacterId,
    string QuestId,
    string? RewardChoiceDefinitionId);

// The development quest tools of the Admin menu, which the quest route runs only in the
// Development environment. Accepting goes around the giver, the level, and the required quests.
internal sealed record AdminAcceptQuestCommand(
    Guid CharacterId,
    string QuestId);

// Completes one objective of the active quest's current step: a counted objective reaches its
// count, and a collect objective gets the items it still lacks into carried storage.
internal sealed record AdminCompleteQuestObjectiveCommand(
    Guid CharacterId,
    string QuestId,
    string ObjectiveId);

// Completes every objective of the active quest's current step, which moves it to the next.
internal sealed record AdminCompleteQuestStepCommand(
    Guid CharacterId,
    string QuestId);

// Completes the active quest's steps up to the last, which then waits for the turn-in.
internal sealed record AdminMakeQuestReadyCommand(
    Guid CharacterId,
    string QuestId);

// Forgets a quest as if the character never took it, with every item of its lineage.
internal sealed record AdminResetQuestCommand(
    Guid CharacterId,
    string QuestId);

// Forgets a quest and every quest that requires it, directly or through another.
internal sealed record AdminResetQuestChainCommand(
    Guid CharacterId,
    string QuestId);

// Forgets every quest of the character.
internal sealed record AdminResetQuestsCommand(Guid CharacterId);

// Sets the character's level, from 1 to the highest level, with no experience inside it.
internal sealed record AdminSetCharacterLevelCommand(
    Guid CharacterId,
    int Level);

// Grants the character experience, as a quest reward does, and levels up as far as it reaches.
internal sealed record AdminGrantExperienceCommand(
    Guid CharacterId,
    long Experience);

// The persisted operation kinds of the quest commands.
internal static class QuestOperationKinds
{
    public const string AbandonQuestItems = "abandon_quest_items";
    public const string AcceptQuest = "accept_quest";
    public const string AbandonQuest = "abandon_quest";
    public const string RegrantQuestItems = "regrant_quest_items";
    public const string TalkToQuestNpc = "talk_to_quest_npc";
    public const string DeliverQuestItems = "deliver_quest_items";
    public const string CreditMobKill = "credit_mob_kill";
    public const string TurnInQuest = "turn_in_quest";
    public const string AdminAcceptQuest = "admin_accept_quest";
    public const string AdminCompleteQuestObjective = "admin_complete_quest_objective";
    public const string AdminCompleteQuestStep = "admin_complete_quest_step";
    public const string AdminMakeQuestReady = "admin_make_quest_ready";
    public const string AdminResetQuest = "admin_reset_quest";
    public const string AdminResetQuestChain = "admin_reset_quest_chain";
    public const string AdminResetQuests = "admin_reset_quests";
    public const string AdminSetCharacterLevel = "admin_set_character_level";
    public const string AdminGrantExperience = "admin_grant_experience";
}

// The highest experience that the Admin menu's experience tool grants at once.
internal static class AdminExperienceRules
{
    public const long MaximumGrant = 1_000_000_000_000L;
}

// The quests a transaction changed, with the quest log revision after it. A quest without a
// step is no longer active; its completion count says whether it was completed.
internal sealed record ItemTransactionQuestLogChange(
    Guid CharacterId,
    long Revision,
    IReadOnlyList<ItemTransactionQuestChange> Quests);

internal sealed record ItemTransactionQuestChange(
    string QuestId,
    string? StepId,
    IReadOnlyList<ItemTransactionQuestObjectiveProgress> Objectives,
    int CompletionCount);

internal sealed record ItemTransactionQuestObjectiveProgress(string ObjectiveId, int Progress);

// The quest log change of a result, in the section that the worker item response passes
// through as its questLog. Results stored before sections keep it under the same key.
internal static class QuestLogSection
{
    public const string Name = "questLog";

    // The quests a transaction changed, as the quest log holds them after it; a later change
    // replaces an earlier one.
    public static void Set(ItemChangeJournal journal, ItemTransactionQuestLogChange change)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(change);
        journal.SetSection(Name, change);
    }

    public static ItemTransactionQuestLogChange? Read(ItemTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ReadSection<ItemTransactionQuestLogChange>(Name);
    }
}

// The character's progression after a transaction that granted experience or set its level, with
// the experience that counted and the levels it gained, which a set level may make negative.
// HighestMobLevelWithoutExperience is int.MaxValue at the highest level, where no Mob gives any.
internal sealed record ItemTransactionProgressionChange(
    Guid CharacterId,
    int Level,
    long Experience,
    long ExperienceToNextLevel,
    int HighestMobLevelWithoutExperience,
    long ExperienceGained,
    int LevelsGained);

// The progression change of a result, in the section that the worker item response passes
// through as its progression. A result without one has no such key.
internal static class ProgressionSection
{
    public const string Name = "progression";

    public static void Set(
        ItemChangeJournal journal,
        Guid characterId,
        CharacterProgressionSnapshot before,
        CharacterProgressionSnapshot after,
        long experienceGained)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        journal.SetSection(Name, new ItemTransactionProgressionChange(
            characterId,
            after.Level,
            after.Experience,
            after.ExperienceToNextLevel,
            after.HighestMobLevelWithoutExperience,
            experienceGained,
            after.Level - before.Level));
    }

    public static void Set(ItemChangeJournal journal, Guid characterId, CharacterExperienceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Set(journal, characterId, change.Before, change.After, change.Gained);
    }

    public static ItemTransactionProgressionChange? Read(ItemTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ReadSection<ItemTransactionProgressionChange>(Name);
    }
}

// A quest action at an NPC needs a live interaction with that NPC's quest capability, whose
// actor definition id the worker claims for the operation.
internal static class QuestNpcAccess
{
    public const string CapabilityKind = "quest_npc";

    public static ItemTransactionLiveAccess Claim(ItemTransactionLiveAccess access, string? npcActorId)
    {
        ArgumentNullException.ThrowIfNull(access);
        return npcActorId is null ? access : access.WithCapability(CapabilityKind, npcActorId);
    }

    public static void Ensure(ItemActorAuthority authority, string npcActorId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureCapability(
            CapabilityKind,
            npcActorId,
            QuestTransactionErrorCodes.QuestNpcAccessRequired,
            "This quest action requires a validated interaction with the quest's NPC.");
    }
}
