using WorldServer.Config;
using WorldServer.Sessions;

namespace WorldServer.Auth;

public sealed class WorldJoinService(
    AuthServiceClient authServiceClient,
    ActivePlayerSessionStore sessionStore,
    WorldServerConfig config)
{
    public async Task<WorldJoinResult<ActivePlayerSessionResponse>> JoinAsync(
        DebugJoinRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JoinTicket))
        {
            return WorldJoinResult<ActivePlayerSessionResponse>.BadRequest(
                "missing_join_ticket",
                "Join ticket is required.");
        }

        var consumedTicket = await authServiceClient.ConsumeJoinTicketAsync(
            request.JoinTicket.Trim(),
            cancellationToken);

        if (!consumedTicket.Succeeded)
        {
            return WorldJoinResult<ActivePlayerSessionResponse>.Failure(
                consumedTicket.StatusCode,
                consumedTicket.Error!.Code,
                consumedTicket.Error.Message);
        }

        if (!string.Equals(consumedTicket.Value!.WorldId, config.WorldServerId, StringComparison.Ordinal))
        {
            return WorldJoinResult<ActivePlayerSessionResponse>.Conflict(
                "wrong_world",
                $"Join ticket is for world {consumedTicket.Value.WorldId}, not {config.WorldServerId}.");
        }

        var session = ActivePlayerSession.FromJoinTicket(consumedTicket.Value);

        return sessionStore.TryAdd(session)
            ? WorldJoinResult<ActivePlayerSessionResponse>.Success(ActivePlayerSessionResponse.FromSession(session))
            : WorldJoinResult<ActivePlayerSessionResponse>.Conflict(
                "character_already_active",
                "Character already has an active WorldServer session.");
    }
}

