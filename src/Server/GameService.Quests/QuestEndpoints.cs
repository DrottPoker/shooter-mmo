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

        // The worker reads a joining character's quest log with the session's credentials.
        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/quest-log", async (
            Guid simulationSessionId,
            SimulationQuestLogRequest request,
            ClaimsPrincipal principal,
            SimulationQuestLogService questLogService,
            CancellationToken cancellationToken) =>
        {
            var result = await questLogService.ReadAsync(
                principal.GetSimulationWorkerId(),
                simulationSessionId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        return app;
    }
}
