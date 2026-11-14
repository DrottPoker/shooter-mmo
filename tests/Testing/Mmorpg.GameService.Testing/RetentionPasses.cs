using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Testing;

// Runs one retention pass over every module's retention tasks, as the retention host does, with
// windows of the test's choosing. The options are built directly, so a test may use windows
// shorter than the settings allow, also zero, which makes every ended row due.
internal static class RetentionPasses
{
    public static async Task<RetentionPass> RunAsync(
        PostgresIntegrationTestContext context,
        TimeSpan itemHistory,
        TimeSpan sessionHistory,
        int batchSize = 1_000,
        RetentionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var ownMetrics = metrics is null ? new RetentionMetrics() : null;
        var runner = new RetentionRunner(
            context.DataSource,
            context.Services.GetServices<IRetentionTask>(),
            new RetentionOptions(TimeSpan.FromMinutes(1), batchSize, 100, itemHistory, sessionHistory),
            metrics ?? ownMetrics!,
            TimeProvider.System,
            NullLogger<RetentionRunner>.Instance);
        return await runner.RunPassAsync(CancellationToken.None);
    }

    // The task's part of the pass, which every pass reports for every task that ran.
    public static RetentionTaskPass Task(this RetentionPass pass, string name)
    {
        ArgumentNullException.ThrowIfNull(pass);
        return Assert.Single(pass.Tasks, task => task.Task == name);
    }
}
