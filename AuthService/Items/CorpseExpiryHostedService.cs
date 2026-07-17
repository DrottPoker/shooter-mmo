namespace AuthService.Items;

public sealed class CorpseExpiryHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CorpseExpiryHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 64;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ExpireBatchAsync(stoppingToken);
        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ExpireBatchAsync(stoppingToken);
        }
    }

    private async Task ExpireBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var corpseService = scope.ServiceProvider.GetRequiredService<CorpseService>();
            var expiredCount = await corpseService.ExpireDueCorpsesAsync(
                BatchSize,
                cancellationToken);
            if (expiredCount > 0)
            {
                logger.LogInformation(
                    "Expired {ExpiredCorpseCount} durable corpses.",
                    expiredCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Durable corpse expiry cleanup failed.");
        }
    }
}
