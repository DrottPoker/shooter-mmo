using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthService.Auth;
using AuthService.Config;
using AuthService.Http;

namespace AuthService.Items;

public static class StackStressFixtureEndpoints
{
    public static IEndpointRouteBuilder MapStackStressFixtureEndpoints(
        this IEndpointRouteBuilder app,
        StackStressFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return app;
        }

        app.MapPost(
                "/api/development/stack-stress/characters/{characterId:guid}/inventory-fixture",
                async (
                    Guid characterId,
                    StackStressInventoryFixtureRequest request,
                    ClaimsPrincipal principal,
                    HttpContext httpContext,
                    StackStressFixtureService fixtureService,
                    CancellationToken cancellationToken) =>
                {
                    var authorization = Authorize(httpContext, options);
                    if (authorization is not null)
                    {
                        return authorization;
                    }

                    var result = await fixtureService.SeedInventoryAsync(
                        principal.GetAccountId(),
                        characterId,
                        request,
                        cancellationToken);
                    return result.ToHttpResult();
                })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
                "/api/development/stack-stress/loot-hotspot",
                async (
                    StackStressLootHotspotRequest request,
                    HttpContext httpContext,
                    StackStressFixtureService fixtureService,
                    CancellationToken cancellationToken) =>
                {
                    var authorization = Authorize(httpContext, options);
                    if (authorization is not null)
                    {
                        return authorization;
                    }

                    var result = await fixtureService.CreateLootHotspotAsync(
                        request,
                        cancellationToken);
                    return result.ToHttpResult();
                })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        return app;
    }

    private static IResult? Authorize(
        HttpContext httpContext,
        StackStressFixtureOptions options)
    {
        if (httpContext.Connection.RemoteIpAddress is not { } remoteAddress
            || !IPAddress.IsLoopback(remoteAddress))
        {
            return ServiceResult<object>.Forbidden(
                "stack_stress_fixture_loopback_required",
                "Stack stress fixtures can only be created over loopback.")
                .ToHttpResult();
        }

        var suppliedSecret = httpContext.Request.Headers[
            StackStressFixtureOptions.AuthorityHeaderName].ToString();
        if (!FixedTimeEquals(options.AuthoritySecret, suppliedSecret))
        {
            return ServiceResult<object>.Unauthorized(
                "invalid_stack_stress_fixture_authority",
                "Stack stress fixture authority credentials are invalid.")
                .ToHttpResult();
        }

        return null;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
