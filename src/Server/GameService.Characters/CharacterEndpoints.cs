using System.Security.Claims;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Characters;

internal static class CharacterEndpoints
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

        // What character creation offers: the body models and the options of every kind of look.
        app.MapGet("/api/character-appearance", (CharacterService characterService) =>
                Results.Ok(characterService.AppearanceCatalog()))
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        return app;
    }
}
