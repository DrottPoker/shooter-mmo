using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Appearance;

// How a character looks on this worker: its body model, look, and lowered headwear as GameService
// stores them, and for a development bot the outfit it shows, since a bot has no items.
public sealed record CharacterLook(
    SimulationAppearance Appearance,
    IReadOnlyList<CharacterLookItem>? Outfit = null);

// An item that others see on a character, by its equipment slot and definition.
public sealed record CharacterLookItem(string EquipmentSlotId, string DefinitionId);

// The looks of the active characters, keyed by character and fenced by its simulation session,
// each with a version that grows with every change, so the realtime loop notices a change
// without comparing looks. Join and appearance changes write from any thread; the loop reads.
public sealed class CharacterLookStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, Entry> looksByCharacterId = [];
    private long nextVersion;

    public void Register(Guid characterId, Guid simulationSessionId, CharacterLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        lock (syncRoot)
        {
            looksByCharacterId[characterId] = new Entry(simulationSessionId, look, ++nextVersion);
        }
    }

    // Stores the look GameService returned after a change, keeping a bot's outfit; false when the
    // session no longer holds the character.
    public bool TryApply(Guid characterId, Guid simulationSessionId, SimulationAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        lock (syncRoot)
        {
            if (!looksByCharacterId.TryGetValue(characterId, out var entry)
                || entry.SimulationSessionId != simulationSessionId)
            {
                return false;
            }

            looksByCharacterId[characterId] = entry with
            {
                Look = entry.Look with { Appearance = appearance },
                Version = ++nextVersion
            };
            return true;
        }
    }

    public bool TryGet(Guid characterId, Guid simulationSessionId, out CharacterLook? look, out long version)
    {
        lock (syncRoot)
        {
            if (looksByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId)
            {
                look = entry.Look;
                version = entry.Version;
                return true;
            }
        }

        look = null;
        version = 0;
        return false;
    }

    public void Remove(Guid characterId, Guid simulationSessionId)
    {
        lock (syncRoot)
        {
            if (looksByCharacterId.TryGetValue(characterId, out var entry)
                && entry.SimulationSessionId == simulationSessionId)
            {
                looksByCharacterId.Remove(characterId);
            }
        }
    }

    private sealed record Entry(Guid SimulationSessionId, CharacterLook Look, long Version);
}
