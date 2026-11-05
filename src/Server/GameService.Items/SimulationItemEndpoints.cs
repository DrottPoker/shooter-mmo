using System.Security.Claims;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items;

internal static class SimulationItemEndpoints
{
    public static IEndpointRouteBuilder MapSimulationItemEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/item-operations", async (
            Guid simulationSessionId,
            SimulationItemOperationRequest request,
            ClaimsPrincipal principal,
            SimulationItemMutationService mutationService,
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

        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/ammunition-consumptions", async (
            Guid simulationSessionId,
            SimulationAmmunitionConsumptionRequest request,
            ClaimsPrincipal principal,
            SimulationItemMutationService mutationService,
            CancellationToken cancellationToken) =>
        {
            var result = await mutationService.ConsumeAmmunitionAsync(
                principal.GetSimulationWorkerId(),
                simulationSessionId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

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
