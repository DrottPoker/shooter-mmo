using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Telemetry;

namespace Mmorpg.SimulationWorker.GameServiceApi;

public sealed class SimulationJoinService(
    GameServiceClient gameServiceClient,
    ActiveSimulationSessionStore sessionStore,
    CarryStateStore carryStateStore,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity)
{
    // One span covers the ticket consumption and any release a rejected join needs.
    public async Task<SimulationJoinResult<ActiveSimulationSession>> JoinAsync(
        string joinTicket,
        CancellationToken cancellationToken)
    {
        using var activity = SimulationWorkerTelemetry.StartOperation("simulation.join");
        var result = await JoinCoreAsync(joinTicket, cancellationToken);
        if (!result.Succeeded)
        {
            SimulationWorkerTelemetry.RecordFailure(activity, result.Error!.Code);
        }

        return result;
    }

    private async Task<SimulationJoinResult<ActiveSimulationSession>> JoinCoreAsync(
        string joinTicket,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(joinTicket))
        {
            return SimulationJoinResult<ActiveSimulationSession>.BadRequest(
                "missing_join_ticket",
                "Join ticket is required.");
        }

        var requestedAt = DateTime.UtcNow;
        var consumedTicket = await gameServiceClient.ConsumeJoinTicketAsync(
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
            await gameServiceClient.ReleaseSimulationSessionAsync(
                consumedTicket.Value.SimulationSessionId,
                identity.RuntimeId,
                consumedTicket.Value.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "wrong_simulation_assignment",
                "Join ticket does not match this simulation worker assignment.");
        }

        var session = ActiveSimulationSession.FromJoinTicket(consumedTicket.Value, requestedAt);
        var registration = sessionStore.Register(session);

        if (registration == ActiveSimulationSessionRegistration.Conflict)
        {
            await gameServiceClient.ReleaseSimulationSessionAsync(
                session.SimulationSessionId,
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "character_already_active",
                "Character already has a different active simulation session.");
        }

        var carryRegistration = carryStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            session.CarryState,
            out var currentCarryState);
        if (carryRegistration == CarryStateApplyResult.Conflict)
        {
            var invalidated = sessionStore.Invalidate(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                "carry_state_conflict",
                "GameService returned conflicting carry state for one item-state revision.");
            if (invalidated)
            {
                carryStateStore.Remove(session.CharacterId, session.SimulationSessionId);
            }

            await gameServiceClient.ReleaseSimulationSessionAsync(
                session.SimulationSessionId,
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "carry_state_conflict",
                "The authoritative carry state is inconsistent.");
        }

        sessionStore.RefreshCarryState(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            currentCarryState);

        sessionStore.TryGet(session.CharacterId, out var registeredSession);

        return SimulationJoinResult<ActiveSimulationSession>.Success(registeredSession!);
    }
}
