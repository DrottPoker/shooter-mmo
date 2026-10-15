using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Corpses;

public sealed class DurableCorpseRestorationService(
    GameServiceClient gameServiceClient,
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

        var restoredOnce = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await gameServiceClient.RestoreDurableCorpsesAsync(
                config.SimulationWorkerId,
                identity.RuntimeId,
                config.ShardId,
                stoppingToken);
            if (result.Succeeded)
            {
                corpseStore.Replace(result.Value!, config.ShardId);
                if (!restoredOnce)
                {
                    restoredOnce = true;
                    logger.LogInformation(
                        "Restored {CorpseCount} unexpired durable corpses for Shard {ShardId} and runtime {RuntimeId}.",
                        result.Value!.Corpses.Count,
                        config.ShardId,
                        identity.RuntimeId);
                }

                await Task.Delay(
                    config.ItemInteraction.DurableCorpseRefreshInterval,
                    stoppingToken);
                continue;
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
