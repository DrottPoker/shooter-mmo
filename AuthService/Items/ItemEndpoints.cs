using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Items;

public static class ItemEndpoints
{
    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/items/catalog", async (
            ItemCatalogQueryService catalogQueryService,
            CancellationToken cancellationToken) =>
        {
            var result = await catalogQueryService.GetCurrentAsync(cancellationToken);
            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        app.MapGet("/api/characters/{characterId:guid}/inventory", async (
            Guid characterId,
            ClaimsPrincipal principal,
            ItemQueryService itemQueryService,
            CancellationToken cancellationToken) =>
        {
            var result = await itemQueryService.GetCharacterInventoryAsync(
                principal.GetAccountId(),
                characterId,
                cancellationToken);
            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        return app;
    }
}
