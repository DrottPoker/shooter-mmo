using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Worlds;

public static class WorldEndpoints
{
    public static IEndpointRouteBuilder MapWorldEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/worlds");

        group.MapGet("/", async (
            WorldService worldService,
            CancellationToken cancellationToken) =>
        {
            var result = await worldService.ListWorldsAsync(cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/{worldId}/join", async (
            string worldId,
            JoinWorldRequest joinRequest,
            HttpRequest request,
            SessionService sessionService,
            WorldService worldService,
            CancellationToken cancellationToken) =>
        {
            var session = await sessionService.AuthenticateAsync(request, cancellationToken);
            if (!session.Succeeded)
            {
                return session.ToHttpResult();
            }

            var result = await worldService.CreateJoinTicketAsync(
                session.Value!,
                worldId,
                joinRequest,
                cancellationToken);

            return result.ToHttpResult();
        });

        app.MapPost("/api/world-join-tickets/consume", async (
            ConsumeJoinTicketRequest consumeRequest,
            WorldService worldService,
            CancellationToken cancellationToken) =>
        {
            var result = await worldService.ConsumeJoinTicketAsync(consumeRequest, cancellationToken);
            return result.ToHttpResult();
        });

        app.MapPost("/api/world-sessions/{worldSessionId:guid}/heartbeat", async (
            Guid worldSessionId,
            WorldSessionCredentialRequest request,
            WorldSessionService worldSessionService,
            CancellationToken cancellationToken) =>
        {
            var result = await worldSessionService.HeartbeatAsync(
                worldSessionId,
                request,
                cancellationToken);

            return result.ToHttpResult();
        });

        app.MapPost("/api/world-sessions/{worldSessionId:guid}/release", async (
            Guid worldSessionId,
            WorldSessionCredentialRequest request,
            WorldSessionService worldSessionService,
            CancellationToken cancellationToken) =>
        {
            var result = await worldSessionService.ReleaseAsync(
                worldSessionId,
                request,
                cancellationToken);

            return result.ToHttpResult();
        });

        return app;
    }
}
