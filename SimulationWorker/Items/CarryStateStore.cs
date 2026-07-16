using ShooterMmo.GameSimulation;

namespace SimulationWorker.Items;

public sealed class CarryStateStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, CarryStateEntry> statesByCharacterId = [];

    public CarryStateApplyResult Register(
        Guid characterId,
        Guid simulationSessionId,
        PlayerCarryState carryState,
        out PlayerCarryState currentState)
    {
        ArgumentNullException.ThrowIfNull(carryState);
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
                statesByCharacterId[characterId] = new CarryStateEntry(
                    simulationSessionId,
                    carryState);
                currentState = carryState;
                return CarryStateApplyResult.Applied;
            }

            return ApplyMonotonic(
                characterId,
                existing,
                carryState,
                out currentState);
        }
    }

    public CarryStateApplyResult ApplyCommitted(
        Guid characterId,
        Guid simulationSessionId,
        PlayerCarryState carryState,
        out PlayerCarryState currentState)
    {
        ArgumentNullException.ThrowIfNull(carryState);
        lock (syncRoot)
        {
            if (!statesByCharacterId.TryGetValue(characterId, out var existing)
                || existing.SimulationSessionId != simulationSessionId)
            {
                currentState = carryState;
                return CarryStateApplyResult.SessionMismatch;
            }

            return ApplyMonotonic(
                characterId,
                existing,
                carryState,
                out currentState);
        }
    }

    public bool TryGet(
        Guid characterId,
        Guid simulationSessionId,
        out PlayerCarryState? carryState)
    {
        lock (syncRoot)
        {
            if (statesByCharacterId.TryGetValue(characterId, out var existing)
                && existing.SimulationSessionId == simulationSessionId)
            {
                carryState = existing.CarryState;
                return true;
            }

            carryState = null;
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

    private CarryStateApplyResult ApplyMonotonic(
        Guid characterId,
        CarryStateEntry existing,
        PlayerCarryState candidate,
        out PlayerCarryState currentState)
    {
        if (candidate.ItemStateRevision < existing.CarryState.ItemStateRevision)
        {
            currentState = existing.CarryState;
            return CarryStateApplyResult.Stale;
        }

        if (candidate.ItemStateRevision == existing.CarryState.ItemStateRevision)
        {
            currentState = existing.CarryState;
            return candidate.Equals(existing.CarryState)
                ? CarryStateApplyResult.Unchanged
                : CarryStateApplyResult.Conflict;
        }

        statesByCharacterId[characterId] = existing with { CarryState = candidate };
        currentState = candidate;
        return CarryStateApplyResult.Applied;
    }

    private sealed record CarryStateEntry(
        Guid SimulationSessionId,
        PlayerCarryState CarryState);
}

public enum CarryStateApplyResult
{
    Applied,
    Unchanged,
    Stale,
    SessionMismatch,
    Conflict
}
