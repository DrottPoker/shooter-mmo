using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorldServer.Auth;
using WorldServer.Config;

namespace WorldServer.Sessions;

public sealed class WorldSessionHeartbeatService(
    AuthServiceClient authServiceClient,
    WorldSessionReleaseService releaseService,
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
                var result = await releaseService.ReleaseAsync(session, cancellationToken);

                if (result.Succeeded)
                {
                    continue;
                }

                logger.LogWarning(
                    "Could not release world session {WorldSessionId} during shutdown: {Code}.",
                    session.WorldSessionId,
                    result.Code);
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
                    : "The world session is no longer active.";
                sessionStore.Invalidate(
                    session.CharacterId,
                    session.WorldSessionId,
                    session.WorldSessionToken,
                    code,
                    message);
                logger.LogWarning(
                    "Invalidated world session {WorldSessionId} with code {Code} after AuthService returned {StatusCode}.",
                    session.WorldSessionId,
                    code,
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
