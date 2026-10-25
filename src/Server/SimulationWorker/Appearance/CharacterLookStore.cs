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
// without comparing looks. A reconnect keeps the session but brings a new session token and
// registers the look GameService stores then, so a change answered for the token before the
// reconnect never overwrites it. Join and appearance changes write from any thread; the loop
// reads.
public sealed class CharacterLookStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, Entry> looksByCharacterId = [];
    private long nextVersion;

    public void Register(Guid characterId, Guid simulationSessionId, string sessionToken, CharacterLook look)
    {
        ArgumentNullException.ThrowIfNull(sessionToken);
        ArgumentNullException.ThrowIfNull(look);
        lock (syncRoot)
        {
            looksByCharacterId[characterId] = new Entry(simulationSessionId, sessionToken, look, ++nextVersion);
        }
    }

    // Stores the look GameService returned after a change asked with the session token, keeping a
    // bot's outfit; false when the session no longer holds the character or reconnected since.
    public bool TryApply(Guid characterId, Guid simulationSessionId, string sessionToken, SimulationAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        lock (syncRoot)
        {
            if (!looksByCharacterId.TryGetValue(characterId, out var entry)
                || entry.SimulationSessionId != simulationSessionId
                || !string.Equals(entry.SessionToken, sessionToken, StringComparison.Ordinal))
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

    private sealed record Entry(Guid SimulationSessionId, string SessionToken, CharacterLook Look, long Version);
}
