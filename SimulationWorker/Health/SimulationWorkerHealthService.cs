using Mmorpg.Server.Common.Health;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Health;

public sealed class SimulationWorkerHealthService(
    GameServiceClient gameServiceClient,
    SimulationWorkerConfig config)
{
    public async Task<ServiceHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var redisTask = RedisHealthProbe.CheckAsync(
            config.RedisConnectionString,
            config.HealthCheckTimeout,
            cancellationToken);
        var gameServiceTask = gameServiceClient.CheckReadinessAsync(cancellationToken);
        var remoteDependencies = await Task.WhenAll(redisTask, gameServiceTask);
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
