using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Health;

// The worker's health for a container runtime. The worker has no HTTP surface and its image no
// shell, so while it is ready, with its UDP port bound and a valid registration lease, it writes
// the file of SimulationWorker:HealthFilePath every few seconds, and deletes it when it is not
// ready and when it stops. The health check, `probe file <path> <seconds>`, reads the age of the
// file. Without a configured path the service does nothing.
public sealed class SimulationWorkerHealthFileService(
    SimulationWorkerConfig config,
    RealtimeTransportReadiness transportReadiness,
    SimulationWorkerRegistrationLease registrationLease,
    ILogger<SimulationWorkerHealthFileService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    // A health check allows several of these before it calls the worker unhealthy.
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private bool writeFailureLogged;

    // One pass: writes the file while the worker is ready, else removes it.
    public void Update()
    {
        if (config.HealthFilePath is not { } path)
        {
            return;
        }

        try
        {
            if (transportReadiness.IsListening && registrationLease.IsValid)
            {
                File.WriteAllText(path, time.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                writeFailureLogged = false;
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The health check then reports the worker unhealthy; the worker itself plays on.
            if (!writeFailureLogged)
            {
                writeFailureLogged = true;
                logger.LogWarning(
                    "The worker health file {HealthFilePath} cannot be written: {Reason}",
                    path,
                    exception.Message);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.HealthFilePath is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(WriteInterval, time);
        do
        {
            Update();
        }
        while (await WaitForNextAsync(timer, stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (config.HealthFilePath is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stopped worker leaves no file to trust, or the file's age already says so.
            }
        }
    }

    private static async Task<bool> WaitForNextAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
