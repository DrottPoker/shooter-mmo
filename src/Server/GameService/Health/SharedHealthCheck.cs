using Mmorpg.Server.Common.Health;

namespace Mmorpg.GameService.Health;

// Shares one dependency check among the callers that arrive while it runs or within Lifetime after
// it started. The health routes carry no rate limit, so that a prober is never answered 429, and
// each readiness check opens a Redis connection and queries PostgreSQL; without this, a caller that
// asks as often as it can would be a way to make GameService do that work as often. The check runs
// to its end whatever one caller does, and each caller waits for it only as long as its own
// cancellation token allows. Thread-safe.
internal sealed class SharedHealthCheck(TimeSpan lifetime, TimeProvider timeProvider)
{
    private readonly object gate = new();
    private Task<ServiceHealth>? current;
    private long startedAt;

    public Task<ServiceHealth> GetAsync(Func<Task<ServiceHealth>> check, CancellationToken cancellationToken)
    {
        Task<ServiceHealth> shared;
        lock (gate)
        {
            if (current is null
                || (current.IsCompleted && timeProvider.GetElapsedTime(startedAt) >= lifetime))
            {
                startedAt = timeProvider.GetTimestamp();
                current = check();
            }

            shared = current;
        }

        return shared.WaitAsync(cancellationToken);
    }
}
