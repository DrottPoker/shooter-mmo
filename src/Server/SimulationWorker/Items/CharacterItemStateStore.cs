using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.Items;

// The committed item state of every active character, keyed by character and fenced by
// its simulation session. Item-state revisions only move forward: an older state is
// stale, and a different state at the same revision is a conflict. Join, session
// heartbeats, and committed operations write from any thread; the realtime loop reads.
public sealed class CharacterItemStateStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, ItemStateEntry> statesByCharacterId = [];

    public ItemStateApplyResult Register(
        Guid characterId,
        Guid simulationSessionId,
        CharacterItemState state,
        out CharacterItemState currentState)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (characterId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        if (simulationSessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationSessionId));
        }

        lock (syncRoot)
        {
            if (!statesByCharacterId.TryGetValue(characterId, out var existing)
                || existing.SimulationSessionId != simulationSessionId)
            {
                statesByCharacterId[characterId] = new ItemStateEntry(simulationSessionId, state);
                currentState = state;
                return ItemStateApplyResult.Applied;
            }

            return ApplyMonotonic(characterId, existing, state, out currentState);
        }
    }

    public ItemStateApplyResult ApplyCommitted(
        Guid characterId,
        Guid simulationSessionId,
        CharacterItemState state,
        out CharacterItemState currentState)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (syncRoot)
        {
            if (!statesByCharacterId.TryGetValue(characterId, out var existing)
                || existing.SimulationSessionId != simulationSessionId)
            {
                currentState = state;
                return ItemStateApplyResult.SessionMismatch;
            }

            return ApplyMonotonic(characterId, existing, state, out currentState);
        }
    }

    public bool TryGet(
        Guid characterId,
        Guid simulationSessionId,
        out PlayerCarryState? carryState)
    {
        if (TryGetItemState(characterId, simulationSessionId, out var state))
        {
            carryState = state!.Carry;
            return true;
        }

        carryState = null;
        return false;
    }

    public bool TryGetItemState(
        Guid characterId,
        Guid simulationSessionId,
        out CharacterItemState? state)
    {
        lock (syncRoot)
        {
            if (statesByCharacterId.TryGetValue(characterId, out var existing)
                && existing.SimulationSessionId == simulationSessionId)
            {
                state = existing.State;
                return true;
            }

            state = null;
            return false;
        }
    }

    public bool Remove(Guid characterId, Guid simulationSessionId)
    {
        lock (syncRoot)
        {
            return statesByCharacterId.TryGetValue(characterId, out var existing)
                && existing.SimulationSessionId == simulationSessionId
                && statesByCharacterId.Remove(characterId);
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            statesByCharacterId.Clear();
        }
    }

    private ItemStateApplyResult ApplyMonotonic(
        Guid characterId,
        ItemStateEntry existing,
        CharacterItemState candidate,
        out CharacterItemState currentState)
    {
        if (candidate.ItemStateRevision < existing.State.ItemStateRevision)
        {
            currentState = existing.State;
            return ItemStateApplyResult.Stale;
        }

        if (candidate.ItemStateRevision == existing.State.ItemStateRevision)
        {
            currentState = existing.State;
            return candidate.Equals(existing.State)
                ? ItemStateApplyResult.Unchanged
                : ItemStateApplyResult.Conflict;
        }

        statesByCharacterId[characterId] = existing with { State = candidate };
        currentState = candidate;
        return ItemStateApplyResult.Applied;
    }

    private sealed record ItemStateEntry(Guid SimulationSessionId, CharacterItemState State);
}

public enum ItemStateApplyResult
{
    Applied,
    Unchanged,
    Stale,
    SessionMismatch,
    Conflict
}
