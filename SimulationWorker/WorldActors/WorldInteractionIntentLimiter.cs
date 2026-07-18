using SimulationWorker.Realtime;

namespace SimulationWorker.WorldActors;

public sealed class WorldInteractionIntentLimiter
{
    public const string RejectionCode = "world_interaction_rate_limited";
    public const double TokensPerSecond = 8d;
    public const double BurstCapacity = 12d;

    private readonly TokenBucket quota = new(TokensPerSecond, BurstCapacity);

    public bool TryConsume()
    {
        return quota.TryConsume(1d);
    }
}
