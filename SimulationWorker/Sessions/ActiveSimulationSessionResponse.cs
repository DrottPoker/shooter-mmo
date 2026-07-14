namespace SimulationWorker.Sessions;

public sealed record ActiveSimulationSessionResponse(
    Guid SimulationSessionId,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime JoinedAt,
    DateTime SessionExpiresAt,
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }

    public static ActiveSimulationSessionResponse FromSession(ActiveSimulationSession session)
    {
        return new ActiveSimulationSessionResponse(
            session.SimulationSessionId,
            session.AccountId,
            session.CharacterId,
            session.CharacterName,
            session.ShardId,
            session.WorldId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.JoinedAt,
            session.SessionExpiresAt,
            session.IsReconnect)
        {
            IsSyntheticBot = session.IsSyntheticBot
        };
    }
}
