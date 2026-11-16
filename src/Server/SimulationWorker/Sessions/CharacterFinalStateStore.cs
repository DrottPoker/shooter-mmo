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
// again after a failure. A state that stays for a reconnect's join after a refused release has a
// time until which it is kept, so one whose reconnect never comes does not stay for good.
// Synchronized; any thread may use it.
public sealed class CharacterFinalStateStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<(Guid CharacterId, Guid SimulationSessionId), CharacterFinalState> states = [];
    private readonly Dictionary<(Guid CharacterId, Guid SimulationSessionId), DateTime> keptUntilUtc = [];

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
                // A newer state belongs to a release that is still to run.
                states[key] = state;
                keptUntilUtc.Remove(key);
            }
        }
    }

    // Keeps the state with the sequence only until the time: its release was refused, and only a
    // reconnect's join can still take it.
    public void KeepUntil(Guid characterId, Guid simulationSessionId, long sequence, DateTime untilUtc)
    {
        lock (syncRoot)
        {
            var key = (characterId, simulationSessionId);
            if (states.TryGetValue(key, out var current) && current.Sequence == sequence)
            {
                keptUntilUtc[key] = untilUtc;
            }
        }
    }

    // Forgets every state whose time ran out and returns how many.
    public int RemoveExpired(DateTime utcNow)
    {
        lock (syncRoot)
        {
            List<(Guid CharacterId, Guid SimulationSessionId)>? expired = null;
            foreach (var (key, until) in keptUntilUtc)
            {
                if (until <= utcNow)
                {
                    (expired ??= []).Add(key);
                }
            }

            if (expired is null)
            {
                return 0;
            }

            foreach (var key in expired)
            {
                states.Remove(key);
                keptUntilUtc.Remove(key);
            }

            return expired.Count;
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
            keptUntilUtc.Remove((characterId, simulationSessionId));
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
                keptUntilUtc.Remove(key);
            }
        }
    }
}
