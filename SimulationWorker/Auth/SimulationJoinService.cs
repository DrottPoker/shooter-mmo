using SimulationWorker.Config;
using SimulationWorker.Registry;
using SimulationWorker.Sessions;

namespace SimulationWorker.Auth;

public sealed class SimulationJoinService(
    AuthServiceClient authServiceClient,
    ActiveSimulationSessionStore sessionStore,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity)
{
    public async Task<SimulationJoinResult<ActiveSimulationSession>> JoinAsync(
        string joinTicket,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(joinTicket))
        {
            return SimulationJoinResult<ActiveSimulationSession>.BadRequest(
                "missing_join_ticket",
                "Join ticket is required.");
        }

        var consumedTicket = await authServiceClient.ConsumeJoinTicketAsync(
            joinTicket.Trim(),
            config.SimulationWorkerId,
            identity.RuntimeId,
            config.ShardId,
            cancellationToken);

        if (!consumedTicket.Succeeded)
        {
            return SimulationJoinResult<ActiveSimulationSession>.Failure(
                consumedTicket.StatusCode,
                consumedTicket.Error!.Code,
                consumedTicket.Error.Message);
        }

        if (!string.Equals(consumedTicket.Value!.WorkerId, config.SimulationWorkerId, StringComparison.Ordinal)
            || !string.Equals(consumedTicket.Value.WorkerRuntimeId, identity.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(consumedTicket.Value.ShardId, config.ShardId, StringComparison.Ordinal)
            || !string.Equals(consumedTicket.Value.WorldId, config.WorldId, StringComparison.Ordinal))
        {
            await authServiceClient.ReleaseSimulationSessionAsync(
                consumedTicket.Value.SimulationSessionId,
                identity.RuntimeId,
                consumedTicket.Value.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "wrong_simulation_assignment",
                "Join ticket does not match this simulation worker assignment.");
        }

        var session = ActiveSimulationSession.FromJoinTicket(consumedTicket.Value);
        var registration = sessionStore.Register(session);

        if (registration == ActiveSimulationSessionRegistration.Conflict)
        {
            await authServiceClient.ReleaseSimulationSessionAsync(
                session.SimulationSessionId,
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "character_already_active",
                "Character already has a different active simulation session.");
        }

        sessionStore.TryGet(session.CharacterId, out var registeredSession);

        return SimulationJoinResult<ActiveSimulationSession>.Success(registeredSession!);
    }
}
