using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Platform.Hosting;

// Whether the process stops. From the stop signal on, readiness reports "stopping", so a proxy
// takes GameService out of rotation while it still serves.
public sealed class ServiceStopState
{
    private int stopping;

    public bool IsStopping => Volatile.Read(ref stopping) == 1;

    internal void MarkStopping()
    {
        Volatile.Write(ref stopping, 1);
    }
}

// The orderly stop: at the stop signal readiness turns to "stopping" at once, then the host waits
// Http:ShutdownDrainSeconds before it stops anything, while Kestrel still accepts and serves
// requests. Only then does the host stop Kestrel, which lets requests in flight finish within the
// rest of Http:ShutdownTimeoutSeconds, and the background services.
internal sealed class ShutdownDrainService(
    ServiceStopState state,
    PlatformOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<ShutdownDrainService> logger) : IHostedLifecycleService
{
    private CancellationTokenRegistration stoppingRegistration;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        // A signal raises ApplicationStopping before the host calls StoppingAsync.
        stoppingRegistration = lifetime.ApplicationStopping.Register(state.MarkStopping);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        state.MarkStopping();
        var drain = options.Http.ShutdownDrain;
        if (drain <= TimeSpan.Zero)
        {
            return;
        }

        logger.LogInformation(
            "GameService is stopping: readiness reports stopping, and requests are still served for {DrainSeconds} s before the server stops.",
            drain.TotalSeconds);
        try
        {
            await Task.Delay(drain, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        await stoppingRegistration.DisposeAsync();
    }
}
