namespace AuthService.Simulation;

public sealed class ShardWorldRebindBlockedException : InvalidOperationException
{
    public ShardWorldRebindBlockedException(
        string shardId,
        string currentWorldId,
        string requestedWorldId,
        long activeAssignments,
        long pendingJoinTickets,
        long activeSimulationSessions,
        long openCorpses)
        : base(CreateMessage(
            shardId,
            currentWorldId,
            requestedWorldId,
            activeAssignments,
            pendingJoinTickets,
            activeSimulationSessions,
            openCorpses))
    {
        ShardId = shardId;
        CurrentWorldId = currentWorldId;
        RequestedWorldId = requestedWorldId;
        ActiveAssignments = activeAssignments;
        PendingJoinTickets = pendingJoinTickets;
        ActiveSimulationSessions = activeSimulationSessions;
        OpenCorpses = openCorpses;
    }

    public string ShardId { get; }

    public string CurrentWorldId { get; }

    public string RequestedWorldId { get; }

    public long ActiveAssignments { get; }

    public long PendingJoinTickets { get; }

    public long ActiveSimulationSessions { get; }

    public long OpenCorpses { get; }

    private static string CreateMessage(
        string shardId,
        string currentWorldId,
        string requestedWorldId,
        long activeAssignments,
        long pendingJoinTickets,
        long activeSimulationSessions,
        long openCorpses)
    {
        return $"Shard '{shardId}' cannot be rebound from world '{currentWorldId}' "
            + $"to world '{requestedWorldId}' while it has {activeAssignments} active assignments, "
            + $"{pendingJoinTickets} pending join tickets, {activeSimulationSessions} active simulation "
            + $"sessions, and {openCorpses} open durable corpses. Stop its worker and drain its "
            + "world-local state before retrying topology reconciliation.";
    }
}
