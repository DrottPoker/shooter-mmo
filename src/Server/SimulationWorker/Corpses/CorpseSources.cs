using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Corpses;

// GameService names a corpse's source with two fields, of which exactly one is set: the name
// of the dead character, or the actor definition of the dead Mob.
public static class CorpseSources
{
    public static bool IsValid(string? characterName, string? actorDefinitionId)
    {
        return string.IsNullOrWhiteSpace(characterName) != string.IsNullOrWhiteSpace(actorDefinitionId);
    }

    public static RealtimeCorpseSource Create(string? characterName, string? actorDefinitionId)
    {
        return string.IsNullOrWhiteSpace(actorDefinitionId)
            ? RealtimeCorpseSource.Character(characterName ?? string.Empty)
            : RealtimeCorpseSource.Actor(actorDefinitionId);
    }
}
