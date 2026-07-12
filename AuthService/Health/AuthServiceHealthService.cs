using AuthService.Config;
using ShooterMmo.Shared.Health;

namespace AuthService.Health;

public sealed class AuthServiceHealthService(
    PostgresHealthProbe postgresHealthProbe,
    AuthServiceConfig config)
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
            "AuthService",
            dependencies.All(dependency => dependency.IsReachable) ? "ready" : "not_ready",
            DateTimeOffset.UtcNow,
            dependencies);
    }
}
