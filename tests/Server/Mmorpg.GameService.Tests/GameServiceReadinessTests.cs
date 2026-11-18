using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Health;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Hosting;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Health;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests;

// Readiness runs the dependency checks of one process once for every caller that asks within a
// second, since the health routes carry no rate limit and each check opens a Redis connection, and
// it lists Redis only while something uses it.
public sealed class GameServiceReadinessTests
{
    [Fact]
    public async Task CallersThatAskWhileACheckRunsOrWithinItsLifetimeShareIt()
    {
        var time = new StepTimeProvider();
        var shared = new SharedHealthCheck(TimeSpan.FromSeconds(1), time);
        var started = 0;
        var gate = new TaskCompletionSource<ServiceHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ServiceHealth> Check()
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        var callers = Enumerable.Range(0, 50)
            .Select(_ => shared.GetAsync(Check, CancellationToken.None))
            .ToArray();
        gate.SetResult(Health("ready"));
        await Task.WhenAll(callers);
        time.Advance(TimeSpan.FromMilliseconds(900));
        await shared.GetAsync(Check, CancellationToken.None);

        Assert.Equal(1, started);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await shared.GetAsync(Check, CancellationToken.None);

        Assert.Equal(2, started);
    }

    // A caller that stops waiting leaves the check running for the others.
    [Fact]
    public async Task ACallerThatGivesUpDoesNotCancelTheCheckOfTheOthers()
    {
        var shared = new SharedHealthCheck(TimeSpan.FromSeconds(1), new StepTimeProvider());
        var gate = new TaskCompletionSource<ServiceHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var impatient = shared.GetAsync(() => gate.Task, cancelled.Token);
        var patient = shared.GetAsync(() => gate.Task, CancellationToken.None);

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        gate.SetResult(Health("ready"));

        Assert.Equal("ready", (await patient).Status);
    }

    [PostgresIntegrationFact]
    public async Task ARedisThatIsDownDoesNotTakeAServiceOutOfRotationThatDoesNotUseIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var options = new PlatformOptions(
            context.ConnectionString,
            // Nothing listens on this port, so a probe of it fails at once.
            "127.0.0.1:1",
            TimeSpan.FromSeconds(1),
            false);
        var postgres = new PostgresHealthProbe(context.DataSource, options);

        var without = await new GameServiceHealthService(
            postgres,
            options,
            new ServiceStopState(),
            RedisAccelerationOptions.Disabled).CheckReadinessAsync(CancellationToken.None);
        var with = await new GameServiceHealthService(
            postgres,
            options,
            new ServiceStopState(),
            RedisAccelerationOptions.Disabled with { Enabled = true }).CheckReadinessAsync(CancellationToken.None);

        Assert.Equal("ready", without.Status);
        Assert.Equal(["postgres"], without.Dependencies.Select(dependency => dependency.Name));
        Assert.Equal("not_ready", with.Status);
        Assert.Equal(["postgres", "redis"], with.Dependencies.Select(dependency => dependency.Name));
    }

    [Fact]
    public async Task AStoppingProcessAnswersStoppingAtOnceWhateverIsCached()
    {
        var options = new PlatformOptions("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", "127.0.0.1:1", TimeSpan.FromMilliseconds(200), false);
        var stop = new ServiceStopState();
        using var dataSource = Npgsql.NpgsqlDataSource.Create(options.PostgresConnectionString);
        var service = new GameServiceHealthService(
            new PostgresHealthProbe(dataSource, options),
            options,
            stop,
            RedisAccelerationOptions.Disabled);
        Assert.Equal("not_ready", (await service.CheckReadinessAsync(CancellationToken.None)).Status);

        typeof(ServiceStopState).GetMethod("MarkStopping", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(stop, null);

        Assert.Equal(GameServiceHealthService.StoppingStatus, (await service.CheckReadinessAsync(CancellationToken.None)).Status);
    }

    private static ServiceHealth Health(string status)
    {
        return new ServiceHealth("GameService", status, DateTimeOffset.UtcNow, []);
    }

    private sealed class StepTimeProvider : TimeProvider
    {
        private long elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref elapsedTicks, duration.Ticks);
        }

        public override long GetTimestamp()
        {
            return TimeSpan.TicksPerDay + Interlocked.Read(ref elapsedTicks);
        }
    }
}
