using System.Security.Claims;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Quests;

internal static class QuestEndpoints
{
    public static IEndpointRouteBuilder MapQuestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/characters/{characterId:guid}/quests", async (
            Guid characterId,
            ClaimsPrincipal principal,
            QuestLogQueryService questLogQueryService,
            CancellationToken cancellationToken) =>
        {
            var result = await questLogQueryService.GetCharacterQuestLogAsync(
                principal.GetAccountId(),
                characterId,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        return app;
    }
}
