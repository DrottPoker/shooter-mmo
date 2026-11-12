using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;

namespace Mmorpg.SimulationWorker.Sessions;

// State the worker keeps for each active character, keyed by character and fenced by its
// simulation session.
public interface ICharacterSessionStore
{
    // Removes the character's state if it still belongs to the session.
    void Remove(Guid characterId, Guid simulationSessionId);

    void Clear();
}

// The occasions on which a store loses state beyond the ones every store shares: teardown of
// a player entity or of a session after an authority failure, and the release of a session
// that no peer owns.
[Flags]
public enum CharacterSessionRemovals
{
    None = 0,

    // Teardown of an entity that a newer session of the character replaced.
    ReplacedEntity = 1,

    // A release that GameService confirmed.
    ConfirmedRelease = 2,

    // The realtime shutdown, which clears the store.
    Shutdown = 4
}

// The per-character stores in registration order. Session teardown and release remove a
// character's state from each store instead of naming it, so a feature that keeps state per
// character registers its store in Create. Filled once before use; any thread may read it.
public sealed class CharacterSessionStores
{
    private readonly List<(ICharacterSessionStore Store, CharacterSessionRemovals AlsoRemovedOn)> stores = [];

    // The worker's stores. The item state, the look, and the quest log each go on today's
    // occasions: only the item state goes with a confirmed release and at shutdown, and the
    // quest log stays when a newer session replaced the entity.
    public static CharacterSessionStores Create(
        CharacterItemStateStore itemStates,
        CharacterLookStore looks,
        CharacterQuestLogStore questLogs)
    {
        return new CharacterSessionStores()
            .Add(
                itemStates,
                CharacterSessionRemovals.ReplacedEntity
                    | CharacterSessionRemovals.ConfirmedRelease
                    | CharacterSessionRemovals.Shutdown)
            .Add(looks, CharacterSessionRemovals.ReplacedEntity)
            .Add(questLogs);
    }

    public CharacterSessionStores Add(
        ICharacterSessionStore store,
        CharacterSessionRemovals alsoRemovedOn = CharacterSessionRemovals.None)
    {
        ArgumentNullException.ThrowIfNull(store);
        stores.Add((store, alsoRemovedOn));
        return this;
    }

    // Removes the character's state for the session from every store.
    public void Remove(Guid characterId, Guid simulationSessionId)
    {
        foreach (var (store, _) in stores)
        {
            store.Remove(characterId, simulationSessionId);
        }
    }

    // Removes the character's state for the session from the stores that go on the occasion.
    public void Remove(CharacterSessionRemovals occasion, Guid characterId, Guid simulationSessionId)
    {
        foreach (var (store, alsoRemovedOn) in stores)
        {
            if ((alsoRemovedOn & occasion) != 0)
            {
                store.Remove(characterId, simulationSessionId);
            }
        }
    }

    // Clears the stores that go on the occasion.
    public void Clear(CharacterSessionRemovals occasion)
    {
        foreach (var (store, alsoRemovedOn) in stores)
        {
            if ((alsoRemovedOn & occasion) != 0)
            {
                store.Clear();
            }
        }
    }
}
