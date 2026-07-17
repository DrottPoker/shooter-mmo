using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Items;

public static class CorpseEndpoints
{
    public static IEndpointRouteBuilder MapCorpseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/player-deaths", async (
            Guid simulationSessionId,
            SimulationPlayerDeathRequest request,
            ClaimsPrincipal principal,
            CorpseService corpseService,
            CancellationToken cancellationToken) =>
        {
            var result = await corpseService.ProcessSimulationDeathAsync(
                principal.GetSimulationWorkerId(),
                simulationSessionId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapGet("/api/simulation-workers/{workerId}/corpses", async (
            string workerId,
            string workerRuntimeId,
            string shardId,
            ClaimsPrincipal principal,
            CorpseService corpseService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(
                    workerId,
                    principal.GetSimulationWorkerId(),
                    StringComparison.Ordinal))
            {
                return ServiceResult<CorpseRestoreResponse>.Forbidden(
                    ItemTransactionErrorCodes.WrongSimulationWorker,
                    "The authenticated simulation worker cannot restore another worker's corpses.")
                    .ToHttpResult();
            }

            var result = await corpseService.ListForWorkerAsync(
                workerId,
                workerRuntimeId,
                shardId,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/simulation-sessions/{simulationSessionId:guid}/corpses/{corpseId:guid}/open",
            async (
                Guid simulationSessionId,
                Guid corpseId,
                SimulationCorpseOpenRequest request,
                ClaimsPrincipal principal,
                CorpseService corpseService,
                CancellationToken cancellationToken) =>
            {
                var result = await corpseService.OpenForSimulationAsync(
                    principal.GetSimulationWorkerId(),
                    simulationSessionId,
                    corpseId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost(
            "/api/simulation-sessions/{simulationSessionId:guid}/corpses/{corpseId:guid}/item-operations",
            async (
                Guid simulationSessionId,
                Guid corpseId,
                SimulationCorpseMutationRequest request,
                ClaimsPrincipal principal,
                CorpseService corpseService,
                CancellationToken cancellationToken) =>
            {
                var result = await corpseService.MutateForSimulationAsync(
                    principal.GetSimulationWorkerId(),
                    simulationSessionId,
                    corpseId,
                    request,
                    cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        return app;
    }
}
