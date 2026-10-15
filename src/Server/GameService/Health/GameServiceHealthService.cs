using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.Server.Common.Health;

namespace Mmorpg.GameService.Health;

public sealed class GameServiceHealthService(
    PostgresHealthProbe postgresHealthProbe,
    PlatformOptions options)
{
    public async Task<ServiceHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var postgresTask = postgresHealthProbe.CheckAsync(cancellationToken);
        var redisTask = RedisHealthProbe.CheckAsync(
            options.RedisConnectionString,
            options.HealthCheckTimeout,
            cancellationToken);
        var dependencies = await Task.WhenAll(postgresTask, redisTask);

        return new ServiceHealth(
            "GameService",
            dependencies.All(dependency => dependency.IsReachable) ? "ready" : "not_ready",
            DateTimeOffset.UtcNow,
            dependencies);
    }
}
