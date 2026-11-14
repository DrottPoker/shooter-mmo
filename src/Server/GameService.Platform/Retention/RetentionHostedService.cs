namespace Mmorpg.GameService.Platform.Retention;

// Runs a retention pass at start and then every Retention:IntervalSeconds, one pass at a time. A
// pass that outlasts the interval delays the next one instead of overlapping it. Several GameService
// processes may run passes at once: every batch locks its rows with skip locked.
internal sealed class RetentionHostedService(
    RetentionRunner runner,
    RetentionOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Interval, timeProvider);
            do
            {
                await runner.RunPassAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
