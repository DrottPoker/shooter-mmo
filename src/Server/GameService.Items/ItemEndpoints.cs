using System.Security.Claims;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items;

internal static class ItemEndpoints
{
    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/characters/{characterId:guid}/item-state", GetItemStateAsync)
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

        return app;
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
