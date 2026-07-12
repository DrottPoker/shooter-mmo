using System.Text.Json;
using WorldServer.Auth;
using WorldServer.Config;

namespace WorldServer.Sessions;

public sealed class WorldSessionHeartbeatService(
    AuthServiceClient authServiceClient,
    ActivePlayerSessionStore sessionStore,
    WorldServerConfig config,
    ILogger<WorldSessionHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(config.WorldSessionHeartbeatInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var session in sessionStore.ListActiveSessions())
            {
                await HeartbeatAsync(session, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var session in sessionStore.ListActiveSessions())
        {
            try
            {
                var result = await authServiceClient.ReleaseWorldSessionAsync(
                    session.WorldSessionId,
                    session.WorldId,
                    session.WorldSessionToken,
                    cancellationToken);

                if (result.Succeeded)
                {
                    sessionStore.Remove(
                        session.CharacterId,
                        session.WorldSessionId,
                        session.WorldSessionToken);
                }
                else
                {
                    logger.LogWarning(
                        "Could not release world session {WorldSessionId} during shutdown: {Code}.",
                        session.WorldSessionId,
                        result.Error!.Code);
                }
            }
            catch (Exception exception) when (IsExpectedTransportException(exception))
            {
                logger.LogWarning(
                    exception,
                    "Could not release world session {WorldSessionId} during shutdown.",
                    session.WorldSessionId);
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HeartbeatAsync(
        ActivePlayerSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await authServiceClient.HeartbeatWorldSessionAsync(
                session.WorldSessionId,
                session.WorldId,
                session.WorldSessionToken,
                cancellationToken);

            if (result.Succeeded)
            {
                sessionStore.Refresh(
                    session.CharacterId,
                    session.WorldSessionId,
                    session.WorldSessionToken,
                    result.Value!.ExpiresAt);

                return;
            }

            if (result.StatusCode is StatusCodes.Status401Unauthorized
                or StatusCodes.Status404NotFound
                or StatusCodes.Status409Conflict)
            {
                sessionStore.Remove(
                    session.CharacterId,
                    session.WorldSessionId,
                    session.WorldSessionToken);
                logger.LogWarning(
                    "Removed inactive world session {WorldSessionId} after AuthService returned {StatusCode}.",
                    session.WorldSessionId,
                    result.StatusCode);

                return;
            }

            logger.LogWarning(
                "Could not heartbeat world session {WorldSessionId}: {Code}.",
                session.WorldSessionId,
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
                "Could not heartbeat world session {WorldSessionId}.",
                session.WorldSessionId);
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
