using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.SimulationBotClient;

// One of the bot's own items: in a container slot, or in an equipment slot.
public sealed record SimulationBotOwnedItem(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision,
    Guid ContainerId,
    int SlotIndex,
    string EquipmentSlotId)
{
    public bool IsEquipped => EquipmentSlotId.Length > 0;
}

public sealed record SimulationBotItemContainer(Guid ContainerId, long Revision, int SlotCapacity);

// The carried item state of a character at one item-state revision, as the Unity client
// reads it over HTTP after a join or when a committed change reached it only as a newer
// carry state: the permanent inventory, the equipped items, and the equipped Bag's contents.
public sealed record SimulationBotItemSnapshot(
    long ItemStateRevision,
    Guid PermanentInventoryId,
    IReadOnlyList<SimulationBotItemContainer> Containers,
    IReadOnlyList<SimulationBotOwnedItem> Items);

// The bot's own items, patched from the owner item messages as the Unity client patches its
// inventory. A committed change that no message describes, such as a death, reaches the bot
// only as a newer carry state; IsBehindServer then says that a full state must be read, as
// the Unity client does.
public sealed class SimulationBotItemView
{
    private readonly Dictionary<Guid, SimulationBotOwnedItem> items = [];
    private readonly Dictionary<Guid, SimulationBotItemContainer> containers = [];

    public bool HasState { get; private set; }

    // The item-state revision the items describe.
    public long Revision { get; private set; } = -1;

    // The newest item-state revision the worker announced.
    public long ServerRevision { get; private set; } = -1;

    public Guid PermanentInventoryId { get; private set; }

    public long Currency { get; private set; }

    public long FullStateLoads { get; private set; }

    public long PatchedChanges { get; private set; }

    public bool IsBehindServer => HasState && ServerRevision > Revision;

    public IReadOnlyCollection<SimulationBotOwnedItem> Items => items.Values;

    public IReadOnlyDictionary<Guid, SimulationBotItemContainer> Containers => containers;

    // A full state replaces the items unless it is older than what the view knows.
    public bool Load(SimulationBotItemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (HasState && snapshot.ItemStateRevision < Revision)
        {
            return false;
        }

        items.Clear();
        containers.Clear();
        foreach (var container in snapshot.Containers)
        {
            containers[container.ContainerId] = container;
        }

        foreach (var item in snapshot.Items)
        {
            items[item.ItemInstanceId] = item;
        }

        PermanentInventoryId = snapshot.PermanentInventoryId;
        Revision = snapshot.ItemStateRevision;
        ServerRevision = Math.Max(ServerRevision, Revision);
        HasState = true;
        FullStateLoads++;
        return true;
    }

    public void ObserveServerRevision(long itemStateRevision)
    {
        ServerRevision = Math.Max(ServerRevision, itemStateRevision);
    }

    // Patches one committed transaction that follows the known revision; any other change
    // leaves the items as they are and only announces the newer revision.
    public bool Apply(RealtimeOwnerItemStateChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ObserveServerRevision(change.CarryState.ItemStateRevision);
        Currency = change.Currency;
        if (!HasState || change.PreviousItemStateRevision != Revision)
        {
            return false;
        }

        foreach (var container in change.ContainerRevisions)
        {
            if (containers.TryGetValue(container.ContainerId, out var known))
            {
                containers[container.ContainerId] = known with { Revision = container.Revision };
            }
        }

        foreach (var item in change.Items)
        {
            switch (item.Location)
            {
                case RealtimeItemStateLocationKind.Removed:
                    items.Remove(item.ItemInstanceId);
                    break;
                case RealtimeItemStateLocationKind.Container:
                    items[item.ItemInstanceId] = new SimulationBotOwnedItem(
                        item.ItemInstanceId,
                        item.DefinitionId,
                        item.Quantity,
                        item.Revision,
                        item.ContainerId,
                        item.SlotIndex,
                        string.Empty);
                    break;
                case RealtimeItemStateLocationKind.Equipment:
                    items[item.ItemInstanceId] = new SimulationBotOwnedItem(
                        item.ItemInstanceId,
                        item.DefinitionId,
                        item.Quantity,
                        item.Revision,
                        Guid.Empty,
                        -1,
                        item.EquipmentSlotId);
                    break;
            }
        }

        Revision = change.CarryState.ItemStateRevision;
        PatchedChanges++;
        return true;
    }

    // Spent arrows change one equipped stack without a client intent.
    public bool Apply(RealtimeOwnerItemQuantityChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ObserveServerRevision(change.CarryState.ItemStateRevision);
        if (!HasState
            || change.PreviousItemStateRevision != Revision
            || !items.TryGetValue(change.ItemInstanceId, out var item))
        {
            return false;
        }

        if (change.Quantity <= 0)
        {
            items.Remove(change.ItemInstanceId);
        }
        else
        {
            items[change.ItemInstanceId] = item with
            {
                Quantity = change.Quantity,
                Revision = change.ItemRevision
            };
        }

        Revision = change.CarryState.ItemStateRevision;
        PatchedChanges++;
        return true;
    }

    public SimulationBotOwnedItem? FindEquipped(string equipmentSlotId)
    {
        return items.Values.FirstOrDefault(item =>
            string.Equals(item.EquipmentSlotId, equipmentSlotId, StringComparison.Ordinal));
    }

    // The carried, unequipped item of a definition in the lowest slot, or null.
    public SimulationBotOwnedItem? FindCarried(string definitionId)
    {
        return items.Values
            .Where(item => !item.IsEquipped
                && string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal))
            .OrderBy(item => item.ContainerId == PermanentInventoryId ? 0 : 1)
            .ThenBy(item => item.SlotIndex)
            .FirstOrDefault();
    }

    public int CountFreeSlots(Guid containerId)
    {
        if (!containers.TryGetValue(containerId, out var container))
        {
            return 0;
        }

        var used = items.Values.Count(item => item.ContainerId == containerId);
        return Math.Max(0, container.SlotCapacity - used);
    }

    public long ContainerRevision(Guid containerId)
    {
        return containers.TryGetValue(containerId, out var container) ? container.Revision : 0;
    }
}
