using System.Security.Claims;

namespace AuthService.Auth;

public static class AuthenticationPrincipalExtensions
{
    public static Guid GetAccountId(this ClaimsPrincipal principal)
    {
        return ReadGuidClaim(principal, AuthenticationConstants.AccountIdClaim);
    }

    public static Guid GetSessionId(this ClaimsPrincipal principal)
    {
        return ReadGuidClaim(principal, AuthenticationConstants.SessionIdClaim);
    }

    public static string GetSimulationWorkerId(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(AuthenticationConstants.SimulationWorkerIdClaim)
            ?? throw new InvalidOperationException(
                "The authenticated simulation worker has no worker id claim.");
    }

    private static Guid ReadGuidClaim(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirstValue(claimType);
        return Guid.TryParse(value, out var id)
            ? id
            : throw new InvalidOperationException($"The authenticated principal has no valid {claimType} claim.");
    }
}
