using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Hosting;
using Mmorpg.Server.Common.Health;

namespace Mmorpg.GameService.Health;

public sealed class GameServiceHealthService(
    PostgresHealthProbe postgresHealthProbe,
    PlatformOptions options,
    ServiceStopState stopState)
{
    public const string StoppingStatus = "stopping";

    public async Task<ServiceHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        // A stopping process is unready whatever its dependencies say, so a proxy stops sending
        // it requests during the drain.
        if (stopState.IsStopping)
        {
            return new ServiceHealth("GameService", StoppingStatus, DateTimeOffset.UtcNow, []);
        }

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
