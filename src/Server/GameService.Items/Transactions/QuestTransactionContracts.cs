using System.Text.Json;
using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.GameService.Items;

// The quest commands with their operation kinds, the quest log change that their results carry,
// and the quest NPC capability that the worker claims for them.

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

// Counts one credited kill of a Mob for every matching kill objective of the character's
// current quest steps.
internal sealed record CreditQuestKillCommand(
    Guid CharacterId,
    string MobActorId,
    IReadOnlyList<string> MobTags);

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

internal static partial class ItemOperationKinds
{
    public const string AbandonQuestItems = "abandon_quest_items";
    public const string AcceptQuest = "accept_quest";
    public const string AbandonQuest = "abandon_quest";
    public const string RegrantQuestItems = "regrant_quest_items";
    public const string TalkToQuestNpc = "talk_to_quest_npc";
    public const string DeliverQuestItems = "deliver_quest_items";
    public const string CreditQuestKill = "credit_quest_kill";
    public const string TurnInQuest = "turn_in_quest";
    public const string AdminAcceptQuest = "admin_accept_quest";
    public const string AdminCompleteQuestObjective = "admin_complete_quest_objective";
    public const string AdminCompleteQuestStep = "admin_complete_quest_step";
    public const string AdminMakeQuestReady = "admin_make_quest_ready";
    public const string AdminResetQuest = "admin_reset_quest";
    public const string AdminResetQuestChain = "admin_reset_quest_chain";
    public const string AdminResetQuests = "admin_reset_quests";
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
    public const string Name = SimulationItemOperationResponse.QuestLogSectionName;

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
        return Read(result.Section(Name));
    }

    public static ItemTransactionQuestLogChange? Read(SimulationItemOperationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Read(response.QuestLog);
    }

    private static ItemTransactionQuestLogChange? Read(JsonElement? section)
    {
        return section is { ValueKind: not JsonValueKind.Null } value
            ? value.Deserialize<ItemTransactionQuestLogChange>(ItemOperationJson.Options)
            : null;
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
            ItemTransactionErrorCodes.QuestNpcAccessRequired,
            "This quest action requires a validated interaction with the quest's NPC.");
    }
}
