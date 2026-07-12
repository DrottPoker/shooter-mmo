using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;
using ShooterMmo.Shared.Http;

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
            ClaimsPrincipal principal,
            WorldService worldService,
            CancellationToken cancellationToken) =>
        {
            var result = await worldService.CreateJoinTicketAsync(
                new AuthenticatedAccount(
                    principal.GetAccountId(),
                    principal.Identity!.Name!,
                    principal.GetSessionId()),
                worldId,
                joinRequest,
                cancellationToken);

            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/world-join-tickets/consume", async (
            ConsumeJoinTicketRequest consumeRequest,
            ClaimsPrincipal principal,
            WorldService worldService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(consumeRequest.WorldId, principal.GetWorldId(), StringComparison.Ordinal))
            {
                return ServiceResult<ConsumedJoinTicketResponse>.Forbidden(
                    "service_world_mismatch",
                    "The authenticated WorldServer cannot act for another world.")
                    .ToHttpResult();
            }

            var result = await worldService.ConsumeJoinTicketAsync(consumeRequest, cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.WorldServerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/world-sessions/{worldSessionId:guid}/heartbeat", async (
            Guid worldSessionId,
            WorldSessionCredentialRequest request,
            ClaimsPrincipal principal,
            WorldSessionService worldSessionService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(request.WorldId, principal.GetWorldId(), StringComparison.Ordinal))
            {
                return ServiceResult<WorldSessionLeaseResponse>.Forbidden(
                    "service_world_mismatch",
                    "The authenticated WorldServer cannot act for another world.")
                    .ToHttpResult();
            }

            var result = await worldSessionService.HeartbeatAsync(
                worldSessionId,
                request,
                cancellationToken);

            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.WorldServerPolicy);

        app.MapPost("/api/world-sessions/{worldSessionId:guid}/release", async (
            Guid worldSessionId,
            WorldSessionCredentialRequest request,
            ClaimsPrincipal principal,
            WorldSessionService worldSessionService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(request.WorldId, principal.GetWorldId(), StringComparison.Ordinal))
            {
                return ServiceResult<WorldSessionLeaseResponse>.Forbidden(
                    "service_world_mismatch",
                    "The authenticated WorldServer cannot act for another world.")
                    .ToHttpResult();
            }

            var result = await worldSessionService.ReleaseAsync(
                worldSessionId,
                request,
                cancellationToken);

            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.WorldServerPolicy);

        return app;
    }
}
