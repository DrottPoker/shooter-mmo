using ShooterMmo.Shared.Health;
using SimulationWorker.Auth;
using SimulationWorker.Config;

namespace SimulationWorker.Health;

public sealed class SimulationWorkerHealthService(
    AuthServiceClient authServiceClient,
    SimulationWorkerConfig config)
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
            "SimulationWorker",
            dependencies.All(dependency => dependency.IsReachable) ? "ready" : "not_ready",
            DateTimeOffset.UtcNow,
            dependencies);
    }
}
