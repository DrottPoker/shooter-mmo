using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimulationWorker.Registry;

public sealed class SimulationWorkerRegistrationLeaseMonitor(
    SimulationWorkerRegistrationLease registrationLease,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SimulationWorkerRegistrationLeaseMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!registrationLease.HasLease || registrationLease.IsValid)
            {
                continue;
            }

            logger.LogCritical(
                "Simulation worker registration lease expired at {ExpiresAtUtc}. Stopping the process to prevent split-brain simulation.",
                registrationLease.ExpiresAtUtc);
            applicationLifetime.StopApplication();
            return;
        }
    }
}
