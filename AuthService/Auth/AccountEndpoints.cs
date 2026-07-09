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
        });

        group.MapPost("/login", async (
            LoginAccountRequest request,
            AccountService accountService,
            CancellationToken cancellationToken) =>
        {
            var result = await accountService.LoginAsync(request, cancellationToken);
            return result.ToHttpResult();
        });

        group.MapGet("/me", async (
            HttpRequest request,
            SessionService sessionService,
            AccountService accountService,
            CancellationToken cancellationToken) =>
        {
            var session = await sessionService.AuthenticateAsync(request, cancellationToken);
            if (!session.Succeeded)
            {
                return session.ToHttpResult();
            }

            var result = await accountService.GetProfileAsync(session.Value!.AccountId, cancellationToken);
            return result.ToHttpResult();
        });

        return app;
    }
}

