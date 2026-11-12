namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record RealtimeShutdownStep(int Order, string Name, Func<Task> Run);

// The flush and clear steps of the realtime shutdown that features register during
// composition, each under its position in RealtimeShutdownOrder. RealtimeServer runs them from
// the lowest to the highest position once the transport stopped and every in-flight operation
// finished. Loop-confined once composed.
internal sealed class RealtimeShutdownSteps
{
    private readonly List<RealtimeShutdownStep> steps = [];

    // The steps in the order the shutdown runs them.
    public IReadOnlyList<RealtimeShutdownStep> Steps => steps;

    public void Add(int order, string name, Action run)
    {
        ArgumentNullException.ThrowIfNull(run);
        AddAsync(order, name, () =>
        {
            run();
            return Task.CompletedTask;
        });
    }

    public void AddAsync(int order, string name, Func<Task> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        var index = 0;
        while (index < steps.Count && steps[index].Order <= order)
        {
            if (steps[index].Order == order)
            {
                throw new InvalidOperationException(
                    $"The shutdown steps '{steps[index].Name}' and '{name}' both take order {order}.");
            }

            index++;
        }

        steps.Insert(index, new RealtimeShutdownStep(order, name, run));
    }
}
