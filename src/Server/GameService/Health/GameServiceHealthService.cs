using Mmorpg.GameService.Config;
using Mmorpg.Server.Common.Health;

namespace Mmorpg.GameService.Health;

public sealed class GameServiceHealthService(
    PostgresHealthProbe postgresHealthProbe,
    GameServiceConfig config)
{
    public async Task<ServiceHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var postgresTask = postgresHealthProbe.CheckAsync(cancellationToken);
        var redisTask = RedisHealthProbe.CheckAsync(
            config.RedisConnectionString,
            config.HealthCheckTimeout,
            cancellationToken);
        var dependencies = await Task.WhenAll(postgresTask, redisTask);

        return new ServiceHealth(
            "GameService",
            dependencies.All(dependency => dependency.IsReachable) ? "ready" : "not_ready",
            DateTimeOffset.UtcNow,
            dependencies);
    }
}
