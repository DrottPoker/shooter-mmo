using WorldServer.Config;
using WorldServer.Sessions;

namespace WorldServer.Auth;

public sealed class WorldJoinService(
    AuthServiceClient authServiceClient,
    ActivePlayerSessionStore sessionStore,
    WorldServerConfig config)
{
    public async Task<WorldJoinResult<ActivePlayerSession>> JoinAsync(
        string joinTicket,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(joinTicket))
        {
            return WorldJoinResult<ActivePlayerSession>.BadRequest(
                "missing_join_ticket",
                "Join ticket is required.");
        }

        var consumedTicket = await authServiceClient.ConsumeJoinTicketAsync(
            joinTicket.Trim(),
            config.WorldServerId,
            cancellationToken);

        if (!consumedTicket.Succeeded)
        {
            return WorldJoinResult<ActivePlayerSession>.Failure(
                consumedTicket.StatusCode,
                consumedTicket.Error!.Code,
                consumedTicket.Error.Message);
        }

        if (!string.Equals(consumedTicket.Value!.WorldId, config.WorldServerId, StringComparison.Ordinal))
        {
            await authServiceClient.ReleaseWorldSessionAsync(
                consumedTicket.Value.WorldSessionId,
                consumedTicket.Value.WorldId,
                consumedTicket.Value.WorldSessionToken,
                cancellationToken);

            return WorldJoinResult<ActivePlayerSession>.Conflict(
                "wrong_world",
                $"Join ticket is for world {consumedTicket.Value.WorldId}, not {config.WorldServerId}.");
        }

        var session = ActivePlayerSession.FromJoinTicket(consumedTicket.Value);
        var registration = sessionStore.Register(session);

        if (registration == ActivePlayerSessionRegistration.Conflict)
        {
            await authServiceClient.ReleaseWorldSessionAsync(
                session.WorldSessionId,
                session.WorldId,
                session.WorldSessionToken,
                cancellationToken);

            return WorldJoinResult<ActivePlayerSession>.Conflict(
                "character_already_active",
                "Character already has a different active WorldServer session.");
        }

        sessionStore.TryGet(session.CharacterId, out var registeredSession);

        return WorldJoinResult<ActivePlayerSession>.Success(registeredSession!);
    }
}
