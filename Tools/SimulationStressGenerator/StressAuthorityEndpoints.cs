namespace ShooterMmo.Tools.SimulationStressGenerator;

public static class StressAuthorityEndpoints
{
    private const string WorkerIdHeader = "X-Simulation-Worker-ID";
    private const string WorkerSecretHeader = "X-Simulation-Worker-Secret";

    public static IEndpointRouteBuilder MapStressAuthority(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/ready", static () => Results.Ok(new { status = "ready" }));

        endpoints.MapPost(
            "/api/simulation-workers/{workerId}/heartbeat",
            static (
                string workerId,
                StressWorkerHeartbeatRequest request,
                HttpContext context,
                StressAuthorityState state) =>
            {
                if (!IsAuthorized(context, state))
                {
                    return Unauthorized();
                }

                return ToResult(state.RegisterWorker(workerId, request));
            });

        endpoints.MapPost(
            "/api/simulation-workers/{workerId}/offline",
            static (
                string workerId,
                StressWorkerOfflineRequest request,
                HttpContext context,
                StressAuthorityState state) =>
            {
                if (!IsAuthorized(context, state))
                {
                    return Unauthorized();
                }

                return ToResult(state.MarkWorkerOffline(workerId, request));
            });

        endpoints.MapPost(
            "/api/simulation-join-tickets/consume",
            static (
                StressConsumeTicketRequest request,
                HttpContext context,
                StressAuthorityState state) =>
            {
                if (!IsAuthorized(context, state))
                {
                    return Unauthorized();
                }

                return ToResult(state.ConsumeTicket(request));
            });

        endpoints.MapPost(
            "/api/simulation-sessions/{sessionId:guid}/heartbeat",
            static (
                Guid sessionId,
                StressSessionCredentialRequest request,
                HttpContext context,
                StressAuthorityState state) =>
            {
                if (!IsAuthorized(context, state))
                {
                    return Unauthorized();
                }

                return ToResult(state.HeartbeatSession(sessionId, request));
            });

        endpoints.MapPost(
            "/api/simulation-sessions/{sessionId:guid}/release",
            static (
                Guid sessionId,
                StressSessionCredentialRequest request,
                HttpContext context,
                StressAuthorityState state) =>
            {
                if (!IsAuthorized(context, state))
                {
                    return Unauthorized();
                }

                return ToResult(state.ReleaseSession(sessionId, request));
            });

        return endpoints;
    }

    private static bool IsAuthorized(HttpContext context, StressAuthorityState state)
    {
        return state.IsAuthorized(
            context.Request.Headers[WorkerIdHeader].FirstOrDefault(),
            context.Request.Headers[WorkerSecretHeader].FirstOrDefault());
    }

    private static IResult Unauthorized()
    {
        return Results.Json(
            new StressProblemDetails(
                "https://shooter-mmo.local/problems/invalid_service_credentials",
                "Unauthorized",
                StatusCodes.Status401Unauthorized,
                "invalid_service_credentials",
                "SimulationWorker did not provide the stress authority credentials."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private static IResult ToResult<T>(StressAuthorityResult<T> result)
    {
        return result.Succeeded
            ? Results.Json(result.Value)
            : Results.Json(result.Problem, statusCode: result.Problem!.Status);
    }
}
