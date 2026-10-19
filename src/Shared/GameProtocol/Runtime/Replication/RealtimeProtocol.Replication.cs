namespace Mmorpg.GameProtocol
{
    // The presentation archetypes of player entities in EntitySpawn.ArchetypeId. The archetype
    // is free text on the wire, so these values are a contract between SimulationWorker and the
    // client that changes neither the wire format nor the protocol version.
    public static class RealtimePlayerArchetypeIds
    {
        // A character that a person plays.
        public const string Player = "player.default";

        // A synthetic development bot from tools/ActiveSimulationBots, presented so players can
        // tell it from a person.
        public const string DevelopmentBot = "player.development_bot";
    }
}
