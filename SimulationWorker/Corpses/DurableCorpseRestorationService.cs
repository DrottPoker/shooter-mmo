using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Registry;

namespace SimulationWorker.Corpses;

public sealed class DurableCorpseRestorationService(
    AuthServiceClient authServiceClient,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    SimulationWorkerRegistrationLease registrationLease,
    DurableCorpseStore corpseStore,
    ILogger<DurableCorpseRestorationService> logger) : BackgroundService
{
    private static readonly TimeSpan AuthorityPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!registrationLease.IsValid)
        {
            await Task.Delay(AuthorityPollInterval, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await authServiceClient.RestoreDurableCorpsesAsync(
                config.SimulationWorkerId,
                identity.RuntimeId,
                config.ShardId,
                stoppingToken);
            if (result.Succeeded)
            {
                corpseStore.Replace(result.Value!, config.ShardId);
                logger.LogInformation(
                    "Restored {CorpseCount} unexpired durable corpses for Shard {ShardId} and runtime {RuntimeId}.",
                    result.Value!.Corpses.Count,
                    config.ShardId,
                    identity.RuntimeId);
                return;
            }

            logger.LogWarning(
                "Durable corpse restoration failed for Shard {ShardId} and runtime {RuntimeId}: {Code}.",
                config.ShardId,
                identity.RuntimeId,
                result.Error!.Code);
            await Task.Delay(RetryInterval, stoppingToken);
        }
    }
}
