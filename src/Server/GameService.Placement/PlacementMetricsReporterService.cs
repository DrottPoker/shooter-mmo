namespace Mmorpg.GameService.Placement;

// Refreshes the placement gauges from the same query that serves the public shard list,
// so dashboards and players see one definition of online, players, and capacity.
internal sealed class PlacementMetricsReporterService(
    IServiceScopeFactory scopeFactory,
    PlacementMetrics metrics,
    PlacementModuleOptions options,
    ILogger<PlacementMetricsReporterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.MetricsInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var shardService = scope.ServiceProvider.GetRequiredService<ShardService>();
            var result = await shardService.ListShardsAsync(cancellationToken);
            metrics.ObserveShards(result.Value!.Select(shard => new PlacementShardState(
                shard.Id,
                shard.IsOnline,
                shard.ActivePlayers,
                shard.Capacity)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Unknown is not online: dropping the series lets the shard alerts fire.
            metrics.ObserveShards([]);
            logger.LogError(exception, "Placement metrics refresh failed.");
        }
    }
}
