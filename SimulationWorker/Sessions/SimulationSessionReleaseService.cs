using System.Net;
using SimulationWorker.Auth;

namespace SimulationWorker.Sessions;

public sealed class SimulationSessionReleaseService(
    AuthServiceClient authServiceClient,
    ActiveSimulationSessionStore sessionStore)
{
    public async Task<SimulationSessionReleaseResult> ReleaseAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        var result = await authServiceClient.ReleaseSimulationSessionAsync(
            session.SimulationSessionId,
            session.WorkerRuntimeId,
            session.SimulationSessionToken,
            cancellationToken);

        if (result.Succeeded
            || result.StatusCode is (int)HttpStatusCode.Unauthorized
                or (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.Conflict)
        {
            sessionStore.Remove(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken);
            return SimulationSessionReleaseResult.Success();
        }

        return SimulationSessionReleaseResult.Failure(
            result.Error!.Code,
            result.Error.Message);
    }
}

public sealed record SimulationSessionReleaseResult(bool Succeeded, string? Code, string? Message)
{
    public static SimulationSessionReleaseResult Success()
    {
        return new SimulationSessionReleaseResult(true, null, null);
    }

    public static SimulationSessionReleaseResult Failure(string code, string message)
    {
        return new SimulationSessionReleaseResult(false, code, message);
    }
}
