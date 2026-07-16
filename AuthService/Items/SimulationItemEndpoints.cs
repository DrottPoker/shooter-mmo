using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Items;

public static class SimulationItemEndpoints
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

        return app;
    }
}
