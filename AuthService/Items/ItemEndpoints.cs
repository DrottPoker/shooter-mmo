using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Items;

public static class ItemEndpoints
{
    private const string CatalogCacheControl = "public, max-age=0, must-revalidate";

    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/item-catalog", GetCatalogAsync)
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);
        app.MapGet("/api/items/catalog", GetCatalogAsync)
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy);

        app.MapGet("/api/characters/{characterId:guid}/item-state", GetItemStateAsync)
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());
        app.MapGet("/api/characters/{characterId:guid}/inventory", GetItemStateAsync)
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapGet("/api/characters/{characterId:guid}/bank", async (
            Guid characterId,
            ClaimsPrincipal principal,
            ItemQueryService itemQueryService,
            CancellationToken cancellationToken) =>
        {
            var result = await itemQueryService.GetCharacterBankAsync(
                principal.GetAccountId(),
                characterId,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapGet("/api/characters/{characterId:guid}/recovery", async (
            Guid characterId,
            ClaimsPrincipal principal,
            ItemQueryService itemQueryService,
            CancellationToken cancellationToken) =>
        {
            var result = await itemQueryService.GetCharacterRecoveryAsync(
                principal.GetAccountId(),
                characterId,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/characters/{characterId:guid}/item-operations/relocate",
            async (
                Guid characterId,
                RelocateAccountItemRequest request,
                ClaimsPrincipal principal,
                AccountItemMutationService mutationService,
                CancellationToken cancellationToken) =>
            {
                var result = await mutationService.RelocateAsync(
                    principal.GetAccountId(),
                    characterId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/characters/{characterId:guid}/item-operations/split",
            async (
                Guid characterId,
                SplitAccountItemStackRequest request,
                ClaimsPrincipal principal,
                AccountItemMutationService mutationService,
                CancellationToken cancellationToken) =>
            {
                var result = await mutationService.SplitAsync(
                    principal.GetAccountId(),
                    characterId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/characters/{characterId:guid}/item-operations/merge",
            async (
                Guid characterId,
                MergeAccountItemStacksRequest request,
                ClaimsPrincipal principal,
                AccountItemMutationService mutationService,
                CancellationToken cancellationToken) =>
            {
                var result = await mutationService.MergeAsync(
                    principal.GetAccountId(),
                    characterId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/characters/{characterId:guid}/item-operations/destroy",
            async (
                Guid characterId,
                DestroyAccountItemRequest request,
                ClaimsPrincipal principal,
                AccountItemMutationService mutationService,
                CancellationToken cancellationToken) =>
            {
                var result = await mutationService.DestroyAsync(
                    principal.GetAccountId(),
                    characterId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/characters/{characterId:guid}/recovery/{deliveryId:guid}/claim",
            async (
                Guid characterId,
                Guid deliveryId,
                ClaimAccountRecoveryDeliveryRequest request,
                ClaimsPrincipal principal,
                AccountItemMutationService mutationService,
                CancellationToken cancellationToken) =>
            {
                var result = await mutationService.ClaimRecoveryAsync(
                    principal.GetAccountId(),
                    characterId,
                    deliveryId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/items/secure-container-tier", async (
            ChangeAccountSecureContainerTierRequest request,
            ClaimsPrincipal principal,
            AccountItemMutationService mutationService,
            CancellationToken cancellationToken) =>
        {
            var result = await mutationService.ChangeSecureContainerTierAsync(
                principal.GetAccountId(),
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        return app;
    }

    private static async Task<IResult> GetCatalogAsync(
        HttpContext httpContext,
        ItemCatalogQueryService catalogQueryService,
        CancellationToken cancellationToken)
    {
        var result = await catalogQueryService.GetCurrentAsync(cancellationToken);
        if (!result.Succeeded)
        {
            return result.ToHttpResult();
        }

        var etag = $"\"{result.Value!.Revision}\"";
        httpContext.Response.Headers.ETag = etag;
        httpContext.Response.Headers.CacheControl = CatalogCacheControl;
        var requestEtags = httpContext.Request.Headers.IfNoneMatch.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestEtags.Any(value => MatchesEtag(value, etag)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Ok(result.Value);
    }

    private static bool MatchesEtag(string requestEtag, string currentEtag)
    {
        if (string.Equals(requestEtag, "*", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = requestEtag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            ? requestEtag[2..].TrimStart()
            : requestEtag;
        return string.Equals(normalized, currentEtag, StringComparison.Ordinal);
    }

    private static async Task<IResult> GetItemStateAsync(
        Guid characterId,
        ClaimsPrincipal principal,
        ItemQueryService itemQueryService,
        CancellationToken cancellationToken)
    {
        var result = await itemQueryService.GetCharacterInventoryAsync(
            principal.GetAccountId(),
            characterId,
            cancellationToken);
        return result.ToHttpResult();
    }
}
