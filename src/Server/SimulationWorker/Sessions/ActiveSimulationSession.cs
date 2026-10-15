using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Sessions;

public sealed record ActiveSimulationSession(
    Guid SimulationSessionId,
    string SimulationSessionToken,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime JoinedAt,
    DateTime SessionExpiresAt,
    PlayerCarryState CarryState,
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }

    // requestedAt is the worker-clock time when the consume request was sent, so the
    // local deadline never outlives the database lease.
    public static ActiveSimulationSession FromJoinTicket(
        ConsumedSimulationJoinTicketResponse ticket,
        DateTime requestedAt)
    {
        return new ActiveSimulationSession(
            ticket.SimulationSessionId,
            ticket.SimulationSessionToken,
            ticket.AccountId,
            ticket.CharacterId,
            ticket.CharacterName,
            ticket.ShardId,
            ticket.WorldId,
            ticket.WorkerId,
            ticket.WorkerRuntimeId,
            DateTime.UtcNow,
            requestedAt.AddMilliseconds(ticket.SessionLeaseDurationMilliseconds),
            new PlayerCarryState(
                ticket.ItemStateRevision,
                ticket.CarriedWeight,
                ticket.CarryCapacity),
            ticket.IsReconnect)
        {
            IsSyntheticBot = ticket.IsSyntheticBot
        };
    }
}
