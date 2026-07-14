namespace ShooterMmo.Tools.ActiveSimulationBots;

public static class ActiveSimulationBotPopulationPolicy
{
    public static int GetAllowedIntentionalLeaves(
        int joinedBots,
        int minimumActiveBots,
        int requestedLeaves)
    {
        if (joinedBots < 0 || minimumActiveBots < 0 || requestedLeaves < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(joinedBots),
                "Population values cannot be negative.");
        }

        return Math.Min(
            requestedLeaves,
            Math.Max(0, joinedBots - minimumActiveBots));
    }

    public static bool CanStartConnection(
        int desiredOnlineBots,
        int targetPopulation,
        int connectionOccupancy,
        int maximumActiveBots)
    {
        if (desiredOnlineBots < 0
            || targetPopulation < 0
            || connectionOccupancy < 0
            || maximumActiveBots < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredOnlineBots),
                "Population values are outside their valid range.");
        }

        return desiredOnlineBots < targetPopulation
            && connectionOccupancy < maximumActiveBots;
    }
}
