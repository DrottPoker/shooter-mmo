using System.Security.Claims;
using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Simulation;

public static class SimulationEndpoints
{
    public static IEndpointRouteBuilder MapSimulationEndpoints(this IEndpointRouteBuilder app)
    {
        var shardGroup = app.MapGroup("/api/shards");

        shardGroup.MapGet("/", async (
            ShardService shardService,
            CancellationToken cancellationToken) =>
        {
            var result = await shardService.ListShardsAsync(cancellationToken);
            return result.ToHttpResult();
        });

        shardGroup.MapPost("/{shardId}/join", async (
            string shardId,
            JoinShardRequest joinRequest,
            ClaimsPrincipal principal,
            ShardService shardService,
            CancellationToken cancellationToken) =>
        {
            var result = await shardService.CreateJoinTicketAsync(
                new AuthenticatedAccount(
                    principal.GetAccountId(),
                    principal.Identity!.Name!,
                    principal.GetSessionId()),
                shardId,
                joinRequest,
                cancellationToken);

            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.AccountSessionPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        var workerGroup = app.MapGroup("/api/simulation-workers");

        workerGroup.MapPost("/{workerId}/heartbeat", async (
            string workerId,
            SimulationWorkerHeartbeatRequest request,
            ClaimsPrincipal principal,
            SimulationWorkerRegistryService registryService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(
                    workerId,
                    principal.GetSimulationWorkerId(),
                    StringComparison.Ordinal))
            {
                return ServiceResult<SimulationWorkerHeartbeatResponse>.Forbidden(
                    "service_worker_mismatch",
                    "The authenticated simulation worker cannot heartbeat another worker identity.")
                    .ToHttpResult();
            }

            var result = await registryService.HeartbeatAsync(
                workerId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy);

        workerGroup.MapPost("/{workerId}/offline", async (
            string workerId,
            SimulationWorkerOfflineRequest request,
            ClaimsPrincipal principal,
            SimulationWorkerRegistryService registryService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(
                    workerId,
                    principal.GetSimulationWorkerId(),
                    StringComparison.Ordinal))
            {
                return ServiceResult<SimulationWorkerOfflineResponse>.Forbidden(
                    "service_worker_mismatch",
                    "The authenticated simulation worker cannot update another worker identity.")
                    .ToHttpResult();
            }

            var result = await registryService.MarkOfflineAsync(
                workerId,
                request,
                cancellationToken);
            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy);

        app.MapPost("/api/simulation-join-tickets/consume", async (
            ConsumeSimulationJoinTicketRequest consumeRequest,
            ClaimsPrincipal principal,
            ShardService shardService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(
                    consumeRequest.WorkerId,
                    principal.GetSimulationWorkerId(),
                    StringComparison.Ordinal))
            {
                return ServiceResult<ConsumedSimulationJoinTicketResponse>.Forbidden(
                    "service_worker_mismatch",
                    "The authenticated simulation worker cannot consume another worker's ticket.")
                    .ToHttpResult();
            }

            var result = await shardService.ConsumeJoinTicketAsync(
                consumeRequest,
                cancellationToken);
            return result.ToHttpResult();
        })
            .RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy)
            .WithMetadata(new SensitiveResponseAttribute());

        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/heartbeat", async (
            Guid simulationSessionId,
            SimulationSessionCredentialRequest request,
            ClaimsPrincipal principal,
            SimulationSessionService simulationSessionService,
            CancellationToken cancellationToken) =>
        {
            var result = await simulationSessionService.HeartbeatAsync(
                simulationSessionId,
                principal.GetSimulationWorkerId(),
                request,
                cancellationToken);

            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy);

        app.MapPost("/api/simulation-sessions/{simulationSessionId:guid}/release", async (
            Guid simulationSessionId,
            SimulationSessionCredentialRequest request,
            ClaimsPrincipal principal,
            SimulationSessionService simulationSessionService,
            CancellationToken cancellationToken) =>
        {
            var result = await simulationSessionService.ReleaseAsync(
                simulationSessionId,
                principal.GetSimulationWorkerId(),
                request,
                cancellationToken);

            return result.ToHttpResult();
        }).RequireAuthorization(AuthenticationConstants.SimulationWorkerPolicy);

        return app;
    }
}
