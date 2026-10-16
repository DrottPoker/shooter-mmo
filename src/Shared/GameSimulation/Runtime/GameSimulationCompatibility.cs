namespace Mmorpg.GameSimulation
{
    public static class GameSimulationCompatibility
    {
        // Bump whenever shared movement or collision behavior changes. The movement
        // determinism golden test pins this value to the expected fingerprint.
        public const string Revision = "movement-simulation-v6";
    }
}
