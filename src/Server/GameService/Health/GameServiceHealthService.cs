using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Hosting;
using Mmorpg.Server.Common.Health;

namespace Mmorpg.GameService.Health;

public sealed class GameServiceHealthService
{
    public const string StoppingStatus = "stopping";

    // How long one dependency check answers every caller that asks meanwhile; shorter than any
    // health check interval, so a probe always sees a state that is a second old at most.
    public static readonly TimeSpan CheckLifetime = TimeSpan.FromSeconds(1);

    private readonly PostgresHealthProbe postgresHealthProbe;
    private readonly PlatformOptions options;
    private readonly ServiceStopState stopState;
    private readonly RedisAccelerationOptions? redisAcceleration;
    private readonly SharedHealthCheck sharedCheck;

    // Redis is a dependency of readiness only while something uses it: with
    // RedisAcceleration:Enabled=false it is neither probed nor listed, so a Redis that is down
    // cannot take a service out of rotation that never talks to it. A null option counts as in use.
    public GameServiceHealthService(
        PostgresHealthProbe postgresHealthProbe,
        PlatformOptions options,
        ServiceStopState stopState,
        RedisAccelerationOptions? redisAcceleration = null,
        TimeProvider? timeProvider = null)
    {
        this.postgresHealthProbe = postgresHealthProbe;
        this.options = options;
        this.stopState = stopState;
        this.redisAcceleration = redisAcceleration;
        sharedCheck = new SharedHealthCheck(CheckLifetime, timeProvider ?? TimeProvider.System);
    }

    public Task<ServiceHealth> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        // A stopping process is unready whatever its dependencies say, so a proxy stops sending
        // it requests during the drain, and this answer is never shared.
        if (stopState.IsStopping)
        {
            return Task.FromResult(
                new ServiceHealth("GameService", StoppingStatus, DateTimeOffset.UtcNow, []));
        }

        return sharedCheck.GetAsync(CheckDependenciesAsync, cancellationToken);
    }

    private async Task<ServiceHealth> CheckDependenciesAsync()
    {
        // Each probe has its own timeout, so the shared check ends whatever its callers do.
        var postgresTask = postgresHealthProbe.CheckAsync(CancellationToken.None);
        var redisTask = redisAcceleration is { Enabled: false }
            ? null
            : RedisHealthProbe.CheckAsync(
                options.RedisConnectionString,
                options.HealthCheckTimeout,
                CancellationToken.None);
        var dependencies = redisTask is null
            ? [await postgresTask]
            : await Task.WhenAll(postgresTask, redisTask);

        return new ServiceHealth(
            "GameService",
            dependencies.All(dependency => dependency.IsReachable) ? "ready" : "not_ready",
            DateTimeOffset.UtcNow,
            dependencies);
    }
}
