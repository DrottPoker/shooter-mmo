using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Quests;

// A character's quest log as GameService last committed it: every active quest with its current
// step and the progress of that step's counted objectives, and how often each quest was
// completed.
public sealed record CharacterQuestLog(
    long Revision,
    IReadOnlyDictionary<string, CharacterActiveQuest> ActiveQuests,
    IReadOnlyDictionary<string, int> CompletionCounts)
{
    public static CharacterQuestLog From(SimulationQuestLogResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new CharacterQuestLog(
            response.QuestLogRevision,
            response.ActiveQuests.ToDictionary(
                quest => quest.QuestId,
                quest => new CharacterActiveQuest(quest.StepId, ToProgress(quest.Objectives)),
                StringComparer.Ordinal),
            response.CompletedQuests.ToDictionary(
                quest => quest.QuestId,
                quest => quest.CompletionCount,
                StringComparer.Ordinal));
    }

    // The log after a committed change: each changed quest as the change names it.
    public CharacterQuestLog With(SimulationQuestLogChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var active = new Dictionary<string, CharacterActiveQuest>(ActiveQuests, StringComparer.Ordinal);
        var completions = new Dictionary<string, int>(CompletionCounts, StringComparer.Ordinal);
        foreach (var quest in change.Quests)
        {
            if (quest.StepId is null)
            {
                active.Remove(quest.QuestId);
            }
            else
            {
                active[quest.QuestId] = new CharacterActiveQuest(quest.StepId, ToProgress(quest.Objectives));
            }

            if (quest.CompletionCount > 0)
            {
                completions[quest.QuestId] = quest.CompletionCount;
            }
            else
            {
                completions.Remove(quest.QuestId);
            }
        }

        return new CharacterQuestLog(change.Revision, active, completions);
    }

    private static IReadOnlyDictionary<string, int> ToProgress(
        IEnumerable<SimulationQuestObjectiveProgress> objectives)
    {
        return objectives.ToDictionary(
            objective => objective.ObjectiveId,
            objective => objective.Progress,
            StringComparer.Ordinal);
    }
}

public sealed record CharacterActiveQuest(string StepId, IReadOnlyDictionary<string, int> Progress);

// The quest logs of the active characters, keyed by character and fenced by its simulation
// session. A join registers the log GameService stores, and every committed change after it
// applies in order, because the authority operations of a character run one at a time. A change
// that is not newer than the log, such as a replay or one already in the log a reconnect read,
// changes nothing. A log that may miss a change, because an action's answer was lost, is stale
// until GameService's log replaces it. Joins and authority operations write from any thread.
public sealed class CharacterQuestLogStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, Entry> logsByCharacterId = [];

    public void Register(Guid characterId, Guid simulationSessionId, CharacterQuestLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (syncRoot)
        {
            logsByCharacterId[characterId] = new Entry(simulationSessionId, log);
        }
    }

    public bool TryGet(Guid characterId, Guid simulationSessionId, out CharacterQuestLog? log)
    {
        lock (syncRoot)
        {
            if (logsByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId)
            {
                log = entry.Log;
                return true;
            }
        }

        log = null;
        return false;
    }

    // Applies a committed change that is newer than the log and returns the revision it
    // applied to.
    public bool TryApply(
        Guid characterId,
        Guid simulationSessionId,
        SimulationQuestLogChange change,
        out long previousRevision)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (syncRoot)
        {
            if (logsByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId
                && change.CharacterId == characterId
                && change.Revision > entry.Log.Revision)
            {
                previousRevision = entry.Log.Revision;
                logsByCharacterId[characterId] = entry with { Log = entry.Log.With(change) };
                return true;
            }
        }

        previousRevision = 0;
        return false;
    }

    public void MarkStale(Guid characterId, Guid simulationSessionId)
    {
        lock (syncRoot)
        {
            if (logsByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId)
            {
                logsByCharacterId[characterId] = entry with { Stale = true };
            }
        }
    }

    public bool IsStale(Guid characterId, Guid simulationSessionId)
    {
        lock (syncRoot)
        {
            return logsByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId
                && entry.Stale;
        }
    }

    // Replaces the log with the one GameService holds, unless it is older, and returns the
    // revision it replaced.
    public bool TryReplace(
        Guid characterId,
        Guid simulationSessionId,
        CharacterQuestLog log,
        out long previousRevision)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (syncRoot)
        {
            if (logsByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId
                && log.Revision >= entry.Log.Revision)
            {
                previousRevision = entry.Log.Revision;
                logsByCharacterId[characterId] = new Entry(simulationSessionId, log);
                return true;
            }
        }

        previousRevision = 0;
        return false;
    }

    public void Remove(Guid characterId, Guid simulationSessionId)
    {
        lock (syncRoot)
        {
            if (logsByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId)
            {
                logsByCharacterId.Remove(characterId);
            }
        }
    }

    private sealed record Entry(Guid SimulationSessionId, CharacterQuestLog Log, bool Stale = false);
}
