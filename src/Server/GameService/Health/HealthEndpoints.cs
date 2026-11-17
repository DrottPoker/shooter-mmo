using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Health;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapGameServiceHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/health/ready"))
            .RequireRateLimiting(PublicRateLimitPolicy.PolicyName);
        app.MapGet("/health/live", () => Results.Ok(new
        {
            service = "GameService",
            status = "live",
            checkedAt = DateTimeOffset.UtcNow
        }))
            .RequireRateLimiting(PublicRateLimitPolicy.PolicyName);
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
        })
            .RequireRateLimiting(PublicRateLimitPolicy.PolicyName);
        return app;
    }
}
