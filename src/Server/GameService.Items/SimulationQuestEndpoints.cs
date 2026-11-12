using System.Security.Claims;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items;

// The worker quest routes: quest actions and kill credits, and the quest drops that a live
// corpse's owner still needs.
internal static class SimulationQuestEndpoints
{
    public static IEndpointRouteBuilder MapSimulationQuestEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/quest-operations", async (
            Guid simulationSessionId,
            SimulationQuestOperationRequest request,
            ClaimsPrincipal principal,
            SimulationQuestMutationService mutationService,
            CancellationToken cancellationToken) =>
        {
            var result = await mutationService.ExecuteAsync(
                principal.GetSimulationWorkerId(),
                simulationSessionId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/quest-drops", async (
            Guid simulationSessionId,
            SimulationQuestDropRequest request,
            ClaimsPrincipal principal,
            SimulationQuestDropService questDropService,
            CancellationToken cancellationToken) =>
        {
            var result = await questDropService.ResolveAsync(
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
