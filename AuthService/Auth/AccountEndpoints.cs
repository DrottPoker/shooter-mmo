using System.Security.Claims;
using AuthService.Http;
using ShooterMmo.Shared.Http;

namespace AuthService.Auth;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts");

        group.MapPost("/register", async (
            RegisterAccountRequest request,
            AccountService accountService,
            CancellationToken cancellationToken) =>
        {
            var result = await accountService.RegisterAsync(request, cancellationToken);
            return result.ToHttpResult();
        })
            .RequireRateLimiting("register")
            .WithMetadata(new SensitiveResponseAttribute());

        group.MapPost("/login", async (
            LoginAccountRequest request,
            AccountService accountService,
            CancellationToken cancellationToken) =>
        {
            var result = await accountService.LoginAsync(request, cancellationToken);
            return result.ToHttpResult();
        })
            .RequireRateLimiting("login")
            .WithMetadata(new SensitiveResponseAttribute());

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            AccountService accountService,
            CancellationToken cancellationToken) =>
        {
            var result = await accountService.GetProfileAsync(principal.GetAccountId(), cancellationToken);
            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        group.MapPost("/logout", async (
            ClaimsPrincipal principal,
            SessionService sessionService,
            CancellationToken cancellationToken) =>
        {
            await sessionService.RevokeAsync(
                principal.GetAccountId(),
                principal.GetSessionId(),
                cancellationToken);

            return Results.NoContent();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        group.MapDelete("/sessions/{sessionId:guid}", async (
            Guid sessionId,
            ClaimsPrincipal principal,
            SessionService sessionService,
            CancellationToken cancellationToken) =>
        {
            await sessionService.RevokeAsync(
                principal.GetAccountId(),
                sessionId,
                cancellationToken);

            return Results.NoContent();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        return app;
    }
}
