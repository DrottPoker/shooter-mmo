using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Health;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapGameServiceHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/health/ready"))
            .RequireRateLimiting(PublicRateLimitPolicy.PolicyName);

        // The two health routes are for the platform's probes, the proxy's and the container's, which
        // come every few seconds from one address. They carry no rate limit, as the worker's routes
        // carry none, so a prober is never answered 429 and taken for a service that is down. They
        // also answer from the stop signal on, so a limit could never be what takes the service out
        // of rotation. Readiness costs one PostgreSQL and one Redis round trip, so
        // GameServiceHealthService shares one check among the callers that ask within a second.
        app.MapGet("/health/live", () => Results.Ok(new
        {
            service = "GameService",
            status = "live",
            checkedAt = DateTimeOffset.UtcNow
        }));
        app.MapGet("/health/ready", async (
            GameServiceHealthService healthService,
            CancellationToken cancellationToken) =>
        {
            var health = await healthService.CheckReadinessAsync(cancellationToken);
            return Results.Json(
                health,
                statusCode: health.Status == "ready"
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
        });
        return app;
    }
}
