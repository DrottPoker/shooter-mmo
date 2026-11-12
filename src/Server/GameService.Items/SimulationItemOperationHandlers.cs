using System.Collections.Frozen;
using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Items;

// The handlers of the worker item route's other operation kinds, by kind, from the modules
// above Items. A kind with two handlers, or a handler of a kind that the route runs itself,
// fails the construction with a message that names both, and the route builds it when its
// endpoints are mapped, so either stops the start.
internal sealed class SimulationItemOperationHandlers
{
    private readonly FrozenDictionary<string, ISimulationItemOperationHandler> byKind;

    public SimulationItemOperationHandlers(IEnumerable<ISimulationItemOperationHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var kinds = new Dictionary<string, ISimulationItemOperationHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            var kind = handler.OperationKind;
            if (string.IsNullOrWhiteSpace(kind) || !string.Equals(kind, kind.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The worker item operation handler {handler.GetType().Name} names no operation kind or one with surrounding white space.");
            }

            if (SimulationItemMutationService.OwnKinds.Contains(kind))
            {
                throw new InvalidOperationException(
                    $"The worker item operation handler {handler.GetType().Name} handles '{kind}', which Items' {nameof(SimulationItemMutationService)} runs itself.");
            }

            if (kinds.TryGetValue(kind, out var other))
            {
                throw new InvalidOperationException(
                    $"Two worker item operation handlers share the operation kind '{kind}': {other.GetType().Name} and {handler.GetType().Name}.");
            }

            kinds.Add(kind, handler);
        }

        byKind = kinds.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Kinds => byKind.Keys;

    public ISimulationItemOperationHandler? Find(string operationKind)
    {
        return byKind.GetValueOrDefault(operationKind);
    }
}
