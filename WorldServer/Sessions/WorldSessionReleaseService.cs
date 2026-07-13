using System.Net;
using WorldServer.Auth;

namespace WorldServer.Sessions;

public sealed class WorldSessionReleaseService(
    AuthServiceClient authServiceClient,
    ActivePlayerSessionStore sessionStore)
{
    public async Task<WorldSessionReleaseResult> ReleaseAsync(
        ActivePlayerSession session,
        CancellationToken cancellationToken)
    {
        var result = await authServiceClient.ReleaseWorldSessionAsync(
            session.WorldSessionId,
            session.WorldId,
            session.WorldSessionToken,
            cancellationToken);

        if (result.Succeeded
            || result.StatusCode is (int)HttpStatusCode.Unauthorized
                or (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.Conflict)
        {
            sessionStore.Remove(
                session.CharacterId,
                session.WorldSessionId,
                session.WorldSessionToken);
            return WorldSessionReleaseResult.Success();
        }

        return WorldSessionReleaseResult.Failure(
            result.Error!.Code,
            result.Error.Message);
    }
}

public sealed record WorldSessionReleaseResult(bool Succeeded, string? Code, string? Message)
{
    public static WorldSessionReleaseResult Success()
    {
        return new WorldSessionReleaseResult(true, null, null);
    }

    public static WorldSessionReleaseResult Failure(string code, string message)
    {
        return new WorldSessionReleaseResult(false, code, message);
    }
}
