using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Registry;

// Stops the worker in order once its last successful registry heartbeat is older than the outage
// tolerance, and tells the players why. Until then the worker plays on without GameService. The
// local registration lease only gates joins; its end no longer stops the worker. A runtime that
// never registered waits for its first heartbeat, as RegistryAcquireTimeout bounds.
public sealed class GameServiceOutageMonitor(
    GameServiceAvailability availability,
    SimulationWorkerConfig config,
    SimulationWorkerStopReason stopReason,
    IHostApplicationLifetime applicationLifetime,
    ILogger<GameServiceOutageMonitor> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, time, stoppingToken);
            if (availability.TimeSinceLastHeartbeat is not { } age
                || age <= config.GameServiceOutageTolerance)
            {
                continue;
            }

            stopReason.Set(GameServiceAvailability.UnavailableCode, GameServiceAvailability.UnavailableMessage);
            logger.LogCritical(
                "GameService has not answered a registry heartbeat for {Age}, longer than the outage tolerance of {Tolerance}. Stopping the worker in order.",
                age,
                config.GameServiceOutageTolerance);
            applicationLifetime.StopApplication();
            return;
        }
    }
}
