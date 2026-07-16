using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;

namespace SimulationWorker.Sessions;

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

    public static ActiveSimulationSession FromJoinTicket(
        ConsumedSimulationJoinTicketResponse ticket)
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
            ticket.SessionExpiresAt,
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
