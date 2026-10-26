using System.Security.Claims;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items;

internal static class CorpseEndpoints
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

        app.MapPost("/api/simulation-workers/{workerId}/mob-corpses/durable", async (
            string workerId,
            CreatePersistentMobCorpseRequest request,
            ClaimsPrincipal principal,
            CorpseService corpseService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(
                    workerId,
                    principal.GetSimulationWorkerId(),
                    StringComparison.Ordinal))
            {
                return ServiceResult<PersistentMobCorpseResponse>.Forbidden(
                    ItemTransactionErrorCodes.WrongSimulationWorker,
                    "The authenticated simulation worker cannot create another worker's Mob corpse.")
                    .ToHttpResult();
            }

            var result = await corpseService.CreatePersistentMobCorpseAsync(
                workerId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/mob-loot-grants", async (
            Guid simulationSessionId,
            SimulationMobLootGrantRequest request,
            ClaimsPrincipal principal,
            CorpseService corpseService,
            CancellationToken cancellationToken) =>
        {
            var result = await corpseService.GrantLiveMobLootAsync(
                principal.GetSimulationWorkerId(),
                simulationSessionId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/mob-loot-grants/loot-all", async (
            Guid simulationSessionId,
            SimulationMobLootAllRequest request,
            ClaimsPrincipal principal,
            CorpseService corpseService,
            CancellationToken cancellationToken) =>
        {
            var result = await corpseService.GrantLiveMobLootAllAsync(
                principal.GetSimulationWorkerId(),
                simulationSessionId,
                request,
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
