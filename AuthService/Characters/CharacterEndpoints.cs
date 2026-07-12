using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Characters;

public static class CharacterEndpoints
{
    public static IEndpointRouteBuilder MapCharacterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/characters");
        group.RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        group.MapGet("/", async (
            ClaimsPrincipal principal,
            CharacterService characterService,
            CancellationToken cancellationToken) =>
        {
            var result = await characterService.ListAsync(principal.GetAccountId(), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/", async (
            ClaimsPrincipal principal,
            CreateCharacterRequest createRequest,
            CharacterService characterService,
            CancellationToken cancellationToken) =>
        {
            var result = await characterService.CreateAsync(
                principal.GetAccountId(),
                createRequest,
                cancellationToken);

            return result.ToHttpResult();
        });

        return app;
    }
}
