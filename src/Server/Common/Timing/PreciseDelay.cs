namespace Mmorpg.Server.Common.Timing;

// Awaitable precise delays for one async loop. A dedicated thread completes each delay with a
// PreciseLoopTimer, because Task.Delay rounds up to the 15.6 ms Windows timer tick. One delay
// can be pending at a time.
public sealed class PreciseDelay : IDisposable
{
    private readonly PreciseLoopTimer timer = new();
    private readonly AutoResetEvent requested = new(false);
    private readonly Thread thread;
    private readonly object gate = new();
    private PendingDelay? pending;
    private bool disposed;

    public PreciseDelay(string threadName)
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName
        };
        thread.Start();
    }

    public bool IsHighResolution => timer.IsHighResolution;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is not null)
            {
                throw new InvalidOperationException("A precise delay is already pending.");
            }

            pending = new PendingDelay(delay, cancellationToken, completion);
        }

        requested.Set();
        return completion.Task;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        requested.Set();
        thread.Join();
        requested.Dispose();
        timer.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            requested.WaitOne();
            PendingDelay? next;
            lock (gate)
            {
                next = pending;
                if (disposed)
                {
                    pending = null;
                    next?.Completion.TrySetCanceled();
                    return;
                }
            }

            if (next is null)
            {
                continue;
            }

            var completed = timer.Wait(next.Delay, next.CancellationToken);
            lock (gate)
            {
                pending = null;
            }

            if (completed)
            {
                next.Completion.TrySetResult();
            }
            else
            {
                next.Completion.TrySetCanceled(next.CancellationToken);
            }
        }
    }

    private sealed record PendingDelay(
        TimeSpan Delay,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);
}
