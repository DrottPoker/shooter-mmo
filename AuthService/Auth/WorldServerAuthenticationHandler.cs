using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuthService.Auth;

public sealed class WorldServerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var worldId = Request.Headers[AuthenticationConstants.WorldServerIdHeader].ToString();
        var suppliedSecret = Request.Headers[AuthenticationConstants.WorldServerSecretHeader].ToString();

        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(suppliedSecret))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var configuredSecret = configuration[$"ServiceAuthentication:WorldServers:{worldId}"];
        if (string.IsNullOrWhiteSpace(configuredSecret)
            && string.Equals(configuration["WORLD_SERVER_ID"], worldId, StringComparison.Ordinal))
        {
            configuredSecret = configuration["WORLD_SERVER_SERVICE_SECRET"];
        }
        if (string.IsNullOrWhiteSpace(configuredSecret)
            || !SecretsMatch(configuredSecret, suppliedSecret))
        {
            return Task.FromResult(AuthenticateResult.Fail("WorldServer credentials are invalid."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, worldId),
            new Claim(ClaimTypes.Name, worldId),
            new Claim(AuthenticationConstants.WorldIdClaim, worldId)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Valid WorldServer service credentials are required.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "invalid_service_credentials"
                })
            .ExecuteAsync(Context);
    }

    private static bool SecretsMatch(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
