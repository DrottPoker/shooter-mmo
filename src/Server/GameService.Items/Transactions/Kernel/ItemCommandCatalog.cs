using System.Collections.Frozen;
using Mmorpg.GameService.Items.Contracts;

namespace Mmorpg.GameService.Items.Transactions;

// Every item command registration of every IItemCommandSource, by command type. Two
// registrations of one command type or of one operation kind fail the construction with a
// message that names both.
internal sealed class ItemCommandCatalog
{
    private readonly FrozenDictionary<Type, ItemCommandRegistration> byCommandType;

    public ItemCommandCatalog(IEnumerable<IItemCommandSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var byType = new Dictionary<Type, Entry>();
        var byKind = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var registrations = new List<ItemCommandRegistration>();
        foreach (var source in sources)
        {
            foreach (var registration in source.Registrations)
            {
                var entry = new Entry(registration, source);
                if (byType.TryGetValue(registration.CommandType, out var sameType))
                {
                    throw Duplicate("command type", sameType, entry);
                }

                if (byKind.TryGetValue(registration.OperationKind, out var sameKind))
                {
                    throw Duplicate("operation kind", sameKind, entry);
                }

                byType.Add(registration.CommandType, entry);
                byKind.Add(registration.OperationKind, entry);
                registrations.Add(registration);
            }
        }

        byCommandType = byType.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Registration);
        Registrations = [.. registrations];
    }

    // In source order, then in each source's order.
    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }

    public ItemCommandRegistration<TCommand> Get<TCommand>()
        where TCommand : notnull
    {
        if (byCommandType.TryGetValue(typeof(TCommand), out var registration))
        {
            return (ItemCommandRegistration<TCommand>)registration;
        }

        throw new InvalidOperationException(
            $"The item command type '{typeof(TCommand).FullName}' has no registration in the item command catalog.");
    }

    private static InvalidOperationException Duplicate(string shared, Entry first, Entry second)
    {
        return new InvalidOperationException(
            $"Two item command registrations share the {shared}: {first} and {second}.");
    }

    private sealed record Entry(ItemCommandRegistration Registration, IItemCommandSource Source)
    {
        public override string ToString()
        {
            return $"{Registration.CommandType.Name} as '{Registration.OperationKind}' from {Source.GetType().Name}";
        }
    }
}
