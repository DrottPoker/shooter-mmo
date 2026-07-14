using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimulationWorker.Auth;
using SimulationWorker.Config;

namespace SimulationWorker.Sessions;

public sealed class SimulationSessionHeartbeatService(
    AuthServiceClient authServiceClient,
    SimulationSessionReleaseService releaseService,
    ActiveSimulationSessionStore sessionStore,
    SimulationWorkerConfig config,
    ILogger<SimulationSessionHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(config.SimulationSessionHeartbeatInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunBoundedAsync(
                sessionStore.ListActiveSessions(),
                HeartbeatAsync,
                stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await RunBoundedAsync(
            sessionStore.ListActiveSessions(),
            async (session, token) =>
            {
                try
                {
                    var result = await releaseService.ReleaseAsync(session, token);

                    if (result.Succeeded)
                    {
                        return;
                    }

                    logger.LogWarning(
                        "Could not release simulation session {SimulationSessionId} during shutdown: {Code}.",
                        session.SimulationSessionId,
                        result.Code);
                }
                catch (Exception exception) when (IsExpectedTransportException(exception))
                {
                    logger.LogWarning(
                        exception,
                        "Could not release simulation session {SimulationSessionId} during shutdown.",
                        session.SimulationSessionId);
                }
            },
            cancellationToken);

        await base.StopAsync(cancellationToken);
    }

    private Task RunBoundedAsync(
        IEnumerable<ActiveSimulationSession> sessions,
        Func<ActiveSimulationSession, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        return BoundedAsync.ForEachAsync(
            sessions,
            config.SimulationSessionHeartbeatMaxConcurrency,
            operation,
            cancellationToken);
    }

    private async Task HeartbeatAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await authServiceClient.HeartbeatSimulationSessionAsync(
                session.SimulationSessionId,
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                cancellationToken);

            if (result.Succeeded)
            {
                sessionStore.Refresh(
                    session.CharacterId,
                    session.SimulationSessionId,
                    session.SimulationSessionToken,
                    result.Value!.ExpiresAt);

                return;
            }

            if (result.StatusCode is (int)HttpStatusCode.Unauthorized
                or (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.Conflict)
            {
                var code = string.Equals(
                    result.Error!.Code,
                    "account_session_replaced",
                    StringComparison.Ordinal)
                    ? "account_session_replaced"
                    : "session_revoked";
                var message = code == "account_session_replaced"
                    ? "This account logged in from another client."
                    : "The simulation session is no longer active.";
                sessionStore.Invalidate(
                    session.CharacterId,
                    session.SimulationSessionId,
                    session.SimulationSessionToken,
                    code,
                    message);
                logger.LogWarning(
                    "Invalidated simulation session {SimulationSessionId} with code {Code} after AuthService returned {StatusCode}.",
                    session.SimulationSessionId,
                    code,
                    result.StatusCode);

                return;
            }

            logger.LogWarning(
                "Could not heartbeat simulation session {SimulationSessionId}: {Code}.",
                session.SimulationSessionId,
                result.Error!.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedTransportException(exception))
        {
            logger.LogWarning(
                exception,
                "Could not heartbeat simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
        }
    }

    private static bool IsExpectedTransportException(Exception exception)
    {
        return exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or NotSupportedException;
    }
}
