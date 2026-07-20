using System.Security.Claims;
using AuthService.Http;

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

        group.MapGet("/session", () => Results.NoContent())
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        group.MapPost("/logout", async (
            ClaimsPrincipal principal,
            SessionService sessionService,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var accountId = principal.GetAccountId();
            var sessionId = principal.GetSessionId();
            var result = await sessionService.RevokeAsync(
                accountId,
                sessionId,
                AccountSessionRevocationReason.Logout,
                cancellationToken);
            if (!result.Succeeded)
            {
                return result.ToHttpResult();
            }

            loggerFactory.CreateLogger("AuthService.Auth").LogInformation(
                "[AUTH] Account {AccountId} logged out and revoked session {SessionId}.",
                accountId,
                sessionId);

            return Results.NoContent();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        group.MapDelete("/sessions/{sessionId:guid}", async (
            Guid sessionId,
            ClaimsPrincipal principal,
            SessionService sessionService,
            CancellationToken cancellationToken) =>
        {
            var result = await sessionService.RevokeAsync(
                principal.GetAccountId(),
                sessionId,
                AccountSessionRevocationReason.ManualRevoke,
                cancellationToken);

            if (!result.Succeeded)
            {
                return result.ToHttpResult();
            }

            return Results.NoContent();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        return app;
    }
}
