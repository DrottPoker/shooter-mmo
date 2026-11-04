using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Quests.Contracts;

// A character's quest log: the active quests with the progress of their current step, the
// completed quests, and a revision that every change raises. A character without any quest
// has revision 0.
public sealed record CharacterQuestLogState(
    long Revision,
    IReadOnlyList<ActiveQuestState> ActiveQuests,
    IReadOnlyList<CompletedQuestState> CompletedQuests);

// Progress holds the counted objectives of the current step by objective id; collect
// objectives count carried items and have none.
public sealed record ActiveQuestState(
    string QuestId,
    string StepId,
    DateTime AcceptedAt,
    IReadOnlyDictionary<string, int> Progress);

public sealed record CompletedQuestState(
    string QuestId,
    int CompletionCount,
    DateTime LastCompletedAt);

// Quest state for other modules, always in the caller's transaction, which already holds the
// character's lock. The caller applies the quest rules; the log only stores their result and
// rejects a write that does not fit the stored state. Every write returns the new revision.
public interface ICharacterQuestLog
{
    Task<CharacterQuestLogState> ReadAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        CancellationToken cancellationToken);

    // Starts the quest at its first step with zero progress for the counted objectives.
    Task<long> AcceptAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        string stepId,
        IReadOnlyList<string> objectiveIds,
        CancellationToken cancellationToken);

    // Sets the progress of counted objectives of the active quest's current step.
    Task<long> SetProgressAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        IReadOnlyDictionary<string, int> progress,
        CancellationToken cancellationToken);

    // Moves the active quest to another step with zero progress for its counted objectives.
    Task<long> AdvanceAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        string stepId,
        IReadOnlyList<string> objectiveIds,
        CancellationToken cancellationToken);

    // Ends the active quest and counts one more completion.
    Task<long> CompleteAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken);

    // Ends the active quest and drops its progress without a completion.
    Task<long> AbandonAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        CancellationToken cancellationToken);
}
