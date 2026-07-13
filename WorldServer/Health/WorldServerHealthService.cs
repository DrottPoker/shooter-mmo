using ShooterMmo.Shared.Health;
using WorldServer.Auth;
using WorldServer.Config;

namespace WorldServer.Health;

public sealed class WorldServerHealthService(
    AuthServiceClient authServiceClient,
    WorldServerConfig config)
{
    public async Task<ServiceHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var redisTask = RedisHealthProbe.CheckAsync(
            config.RedisConnectionString,
            config.HealthCheckTimeout,
            cancellationToken);
        var authServiceTask = authServiceClient.CheckReadinessAsync(cancellationToken);
        var remoteDependencies = await Task.WhenAll(redisTask, authServiceTask);
        var dependencies = remoteDependencies
            .Append(UdpPortHealthProbe.CheckAvailable(config.UdpPort))
            .ToArray();

        return new ServiceHealth(
            "WorldServer",
            dependencies.All(dependency => dependency.IsReachable) ? "ready" : "not_ready",
            DateTimeOffset.UtcNow,
            dependencies);
    }
}
