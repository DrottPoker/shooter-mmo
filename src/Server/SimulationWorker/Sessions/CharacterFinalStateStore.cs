namespace Mmorpg.SimulationWorker.Sessions;

// A character's place and vitals as the worker saves them: position and facing as the
// simulation's floats, and health and mana.
public readonly record struct CharacterStateSnapshot(
    float PositionX,
    float PositionY,
    float PositionZ,
    float YawDegrees,
    int Health,
    int Mana);

// The state a session's character had when the realtime loop let go of it, numbered within the
// session like every state the worker sends.
public readonly record struct CharacterFinalState(long Sequence, CharacterStateSnapshot State);

// The final state of every session whose release is about to run or runs, by character and
// simulation session. The realtime loop captures it when it removes a player entity whose
// session it still holds, such as a lingering body that goes, and at shutdown; whichever path
// then releases the session sends it with the release, which may run later, off the loop, and
// again after a failure. Synchronized; any thread may use it.
public sealed class CharacterFinalStateStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<(Guid CharacterId, Guid SimulationSessionId), CharacterFinalState> states = [];

    public int Count
    {
        get
        {
            lock (syncRoot)
            {
                return states.Count;
            }
        }
    }

    // Keeps the newer of the captured state and one captured before for the same session.
    public void Capture(Guid characterId, Guid simulationSessionId, CharacterFinalState state)
    {
        lock (syncRoot)
        {
            var key = (characterId, simulationSessionId);
            if (!states.TryGetValue(key, out var current) || current.Sequence < state.Sequence)
            {
                states[key] = state;
            }
        }
    }

    public bool TryGet(Guid characterId, Guid simulationSessionId, out CharacterFinalState state)
    {
        lock (syncRoot)
        {
            return states.TryGetValue((characterId, simulationSessionId), out state);
        }
    }

    public void Remove(Guid characterId, Guid simulationSessionId)
    {
        lock (syncRoot)
        {
            states.Remove((characterId, simulationSessionId));
        }
    }

    // Removes the state only while it is the one with the sequence, so a release forgets what it
    // sent and never a newer state that the loop captured meanwhile.
    public void Remove(Guid characterId, Guid simulationSessionId, long sequence)
    {
        lock (syncRoot)
        {
            var key = (characterId, simulationSessionId);
            if (states.TryGetValue(key, out var current) && current.Sequence == sequence)
            {
                states.Remove(key);
            }
        }
    }
}
