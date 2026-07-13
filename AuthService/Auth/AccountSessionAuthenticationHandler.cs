using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuthService.Auth;

public sealed class AccountSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessionService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private string challengeCode = "invalid_session_token";
    private string challengeDetail = "A valid bearer session token is required.";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = SessionService.ReadBearerToken(Request);
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var result = await sessionService.ResolveTokenAsync(token, Context.RequestAborted);
        if (result.Account is null)
        {
            challengeCode = result.ErrorCode ?? challengeCode;
            challengeDetail = result.ErrorMessage ?? challengeDetail;
            return AuthenticateResult.Fail(challengeDetail);
        }

        var account = result.Account;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString()),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(AuthenticationConstants.AccountIdClaim, account.AccountId.ToString()),
            new Claim(AuthenticationConstants.SessionIdClaim, account.SessionId.ToString())
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: challengeDetail,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = challengeCode
                })
            .ExecuteAsync(Context);
    }
}
