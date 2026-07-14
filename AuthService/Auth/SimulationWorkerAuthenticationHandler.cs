using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuthService.Auth;

public sealed class SimulationWorkerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var workerId = Request.Headers[AuthenticationConstants.SimulationWorkerIdHeader].ToString();
        var suppliedSecret = Request.Headers[AuthenticationConstants.SimulationWorkerSecretHeader].ToString();

        if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(suppliedSecret))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!IsValidIdentifier(workerId))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Simulation worker identity is invalid."));
        }

        var configuredSecret = configuration[$"ServiceAuthentication:SimulationWorkers:{workerId}"];
        if (string.IsNullOrWhiteSpace(configuredSecret)
            && string.Equals(configuration["SIMULATION_WORKER_ID"], workerId, StringComparison.Ordinal))
        {
            configuredSecret = configuration["SIMULATION_WORKER_SERVICE_SECRET"];
        }
        if (string.IsNullOrWhiteSpace(configuredSecret)
            || !SecretsMatch(configuredSecret, suppliedSecret))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Simulation worker credentials are invalid."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, workerId),
            new Claim(ClaimTypes.Name, workerId),
            new Claim(AuthenticationConstants.SimulationWorkerIdClaim, workerId)
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
                detail: "Valid simulation worker service credentials are required.",
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

    private static bool IsValidIdentifier(string value)
    {
        return value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
