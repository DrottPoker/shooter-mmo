using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Health;

// The worker's health for a container runtime. The worker has no HTTP surface and its image no
// shell, so while it is ready it writes the file of SimulationWorker:HealthFilePath every few
// seconds, and deletes it when it is not ready and when it stops. The health check,
// `probe file <path> <seconds>`, reads the age of the file. Without a configured path the service
// does nothing.
//
// Ready means three things at once: the UDP port is bound, the registration lease is valid, and
// the realtime loop completed a tick within LoopSilenceLimit. The third is the one that a hung
// worker fails: its port stays bound and another service keeps renewing its lease, but nobody can
// play. A loop that falls silent loses the file and logs one warning for the episode; a loop that
// ticks again gets the file back.
public sealed class SimulationWorkerHealthFileService(
    SimulationWorkerConfig config,
    RealtimeTransportReadiness transportReadiness,
    SimulationWorkerRegistrationLease registrationLease,
    RealtimeLoopLiveness loopLiveness,
    ILogger<SimulationWorkerHealthFileService> logger,
    TimeProvider? timeProvider = null,
    IHostApplicationLifetime? applicationLifetime = null) : BackgroundService
{
    // A health check allows several of these before it calls the worker unhealthy.
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(5);

    // The longest that the loop may go without completing a tick before the worker is not ready.
    // A tick is 33 ms, so this is 150 ticks. The loop never blocks on a load, a database call, or
    // a collision chunk, and its longest legitimate pause is a garbage collection of milliseconds
    // or a catch-up of five ticks, so a silence this long is a hang.
    public static readonly TimeSpan LoopSilenceLimit = TimeSpan.FromSeconds(5);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private bool writeFailureLogged;
    private bool silenceLogged;

    // One pass: writes the file while the worker is ready, else removes it.
    public void Update()
    {
        if (config.HealthFilePath is not { } path)
        {
            return;
        }

        var silence = loopLiveness.SilenceFor(time);
        var loopAlive = silence is { } elapsed && elapsed <= LoopSilenceLimit;
        var stopping = applicationLifetime?.ApplicationStopping.IsCancellationRequested == true;
        var registered = transportReadiness.IsListening && registrationLease.IsValid;
        ReportSilence(registered && !loopAlive && !stopping, silence);

        try
        {
            if (registered && loopAlive && !stopping)
            {
                File.WriteAllText(path, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
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

    // One warning when a worker that would be ready has a silent loop, and none until it ticked
    // again, so a hang that lasts an hour is one line and not seven hundred.
    private void ReportSilence(bool silent, TimeSpan? silence)
    {
        if (!silent)
        {
            silenceLogged = false;
            return;
        }

        if (silenceLogged)
        {
            return;
        }

        silenceLogged = true;
        if (silence is { } elapsed)
        {
            logger.LogWarning(
                "[SIMULATION] The realtime loop completed no tick for {SilentSeconds:0.0} s, more than the {LimitSeconds:0} s that a ready worker allows, so the worker is not ready and its health file is removed.",
                elapsed.TotalSeconds,
                LoopSilenceLimit.TotalSeconds);
        }
        else
        {
            logger.LogWarning(
                "[SIMULATION] The realtime loop has not completed a tick although the worker is registered, so the worker is not ready and has no health file.");
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
