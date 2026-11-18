using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Placement;

// What a Shard's worker runs: its realtime protocol version, its simulation revision, and the
// collision revision of its World, as its last heartbeat reported them.
internal readonly record struct WorkerVersions(
    int ProtocolVersion,
    string SimulationRevision,
    string WorldId,
    string CollisionRevision);

// The version question of a join, decided before a ticket is issued: a client plays only on a
// worker that runs exactly its protocol version and simulation revision, and exactly its collision
// revision of the World. There is no window of versions. The codes are the ones the client shows
// texts for, so a refusal reads the same wherever the client finds it first.
internal static class JoinVersionCheck
{
    // The worker is ahead of the client: the player must update the game.
    public const string IncompatibleProtocol = "incompatible_protocol";

    // The worker is behind the client: a deployment is still replacing it, so the player waits.
    public const string ShardNotUpdated = "shard_not_updated";

    // The same protocol with another simulation revision, which has no order.
    public const string IncompatibleSimulation = "incompatible_simulation";

    // The same protocol and simulation with other baked collision data for the Shard's World,
    // which has no order either.
    public const string IncompatibleWorldData = "incompatible_world_data";

    // The code that refuses a client with these versions on this worker, or null when it fits. A
    // client that names no protocol version, as one from before the versions travelled in the
    // join request, cannot be matched and is refused as one that must update. The collision
    // revision is checked only when the client names the World it belongs to and that is the
    // Shard's World, so a client with a stale Shard list is never told that its data is out of date.
    public static string? Refusal(
        int? protocolVersion,
        string? simulationRevision,
        string? worldId,
        string? collisionRevision,
        WorkerVersions worker)
    {
        if (protocolVersion is not { } protocol || protocol < 1)
        {
            return IncompatibleProtocol;
        }

        if (worker.ProtocolVersion != protocol)
        {
            return worker.ProtocolVersion > protocol ? IncompatibleProtocol : ShardNotUpdated;
        }

        if (!string.Equals(simulationRevision, worker.SimulationRevision, StringComparison.Ordinal))
        {
            return IncompatibleSimulation;
        }

        if (!string.IsNullOrWhiteSpace(worldId)
            && !string.IsNullOrWhiteSpace(collisionRevision)
            && string.Equals(worldId, worker.WorldId, StringComparison.Ordinal)
            && !string.Equals(collisionRevision, worker.CollisionRevision, StringComparison.OrdinalIgnoreCase))
        {
            return IncompatibleWorldData;
        }

        return null;
    }

    // The 409 that refuses the join with the code of Refusal.
    public static ServiceResult<T> Conflict<T>(string code, WorkerVersions worker)
    {
        var reason = code switch
        {
            IncompatibleProtocol => "The simulation worker runs a newer realtime protocol version than the client, or the client did not name its version.",
            ShardNotUpdated => "The simulation worker runs an older realtime protocol version than the client.",
            IncompatibleSimulation => "The simulation worker runs another simulation revision than the client.",
            _ => "The simulation worker runs other collision data for its World than the client."
        };
        return ServiceResult<T>.Conflict(
            code,
            $"{reason} Worker: protocol {worker.ProtocolVersion}, simulation '{worker.SimulationRevision}', World '{worker.WorldId}', collision '{worker.CollisionRevision}'.");
    }
}
