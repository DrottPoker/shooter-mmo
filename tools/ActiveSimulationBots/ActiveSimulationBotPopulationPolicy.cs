namespace Mmorpg.Tools.ActiveSimulationBots;

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

    // Doubles from the minimum with every consecutive failure up to the maximum, then scales
    // by a jitter between 0.75 and 1.25 drawn from jitterSample in [0, 1).
    public static TimeSpan GetRetryDelay(
        int consecutiveFailures,
        TimeSpan minimumRetryDelay,
        TimeSpan maximumRetryDelay,
        double jitterSample)
    {
        if (consecutiveFailures < 1
            || minimumRetryDelay <= TimeSpan.Zero
            || maximumRetryDelay < minimumRetryDelay
            || jitterSample is < 0d or >= 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailures),
                "Retry values are outside their valid range.");
        }

        var exponentialSeconds = minimumRetryDelay.TotalSeconds
            * Math.Pow(2d, Math.Min(consecutiveFailures, 10) - 1);
        var cappedSeconds = Math.Min(maximumRetryDelay.TotalSeconds, exponentialSeconds);
        return TimeSpan.FromSeconds(cappedSeconds * (0.75d + (jitterSample * 0.5d)));
    }
}
