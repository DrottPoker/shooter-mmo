namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record RealtimeShutdownStep(string Name, Func<Task> Run);

// The flush and clear steps of the realtime shutdown that features register during
// composition. RealtimeServer runs them in registration order once the transport stopped and
// every in-flight operation finished. Loop-confined once composed.
internal sealed class RealtimeShutdownSteps
{
    private readonly List<RealtimeShutdownStep> steps = [];

    public IReadOnlyList<RealtimeShutdownStep> Steps => steps;

    public void Add(string name, Action run)
    {
        ArgumentNullException.ThrowIfNull(run);
        AddAsync(name, () =>
        {
            run();
            return Task.CompletedTask;
        });
    }

    public void AddAsync(string name, Func<Task> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        steps.Add(new RealtimeShutdownStep(name, run));
    }
}
