using ShooterMmo.GameProtocol;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed class StressInventoryState
{
    private readonly Dictionary<Guid, ContainerState> containers = [];

    public StressInventoryState(FullStackCharacterInventoryResponse snapshot)
    {
        Apply(snapshot);
    }

    public long CharacterRevision { get; private set; }

    public Guid PermanentInventoryId { get; private set; }

    public Guid SecureContainerId { get; private set; }

    public void Apply(FullStackCharacterInventoryResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CharacterRevision = snapshot.ItemStateRevision;
        PermanentInventoryId = snapshot.PermanentInventory.ContainerId;
        SecureContainerId = snapshot.SecureContainer.Contents.ContainerId;
        containers.Clear();
        Add(snapshot.PermanentInventory);
        Add(snapshot.SecureContainer.Contents);
        if (snapshot.EquippedBag is not null)
        {
            Add(snapshot.EquippedBag.Contents);
        }
    }

    public OwnedItem? FindItem(string definitionId)
    {
        return containers.Values
            .SelectMany(container => container.Slots.Values
                .Where(slot => slot.Item is not null)
                .Select(slot => new OwnedItem(
                    container.ContainerId,
                    container.Revision,
                    slot.SlotIndex,
                    slot.Item!)))
            .FirstOrDefault(item => string.Equals(
                item.Item.DefinitionId,
                definitionId,
                StringComparison.Ordinal));
    }

    public int? FindEmptySlot(Guid containerId)
    {
        return containers.TryGetValue(containerId, out var container)
            ? container.Slots.Values
                .Where(slot => slot.Item is null)
                .OrderBy(slot => slot.SlotIndex)
                .Select(slot => (int?)slot.SlotIndex)
                .FirstOrDefault()
            : null;
    }

    public long GetContainerRevision(Guid containerId)
    {
        return containers.TryGetValue(containerId, out var container)
            ? container.Revision
            : -1;
    }

    public bool ApplyRelocate(
        RealtimeItemOperationIntent intent,
        RealtimeItemOperationResult result)
    {
        var source = containers.Values
            .SelectMany(container => container.Slots.Values.Select(slot => (container, slot)))
            .SingleOrDefault(candidate =>
                candidate.slot.Item?.ItemInstanceId == intent.ItemInstanceId);
        if (source.container is null
            || !containers.TryGetValue(intent.DestinationContainerId, out var destination)
            || !destination.Slots.TryGetValue(intent.DestinationSlotIndex, out var destinationSlot)
            || destinationSlot.Item is not null)
        {
            return false;
        }

        var revision = result.ItemRevisions.SingleOrDefault(item =>
            item.ItemInstanceId == intent.ItemInstanceId)?.Revision;
        if (revision is null)
        {
            return false;
        }

        var moved = source.slot.Item! with { Revision = revision.Value };
        source.container.Slots[source.slot.SlotIndex] = source.slot with { Item = null };
        destination.Slots[destinationSlot.SlotIndex] = destinationSlot with { Item = moved };
        foreach (var containerRevision in result.ContainerRevisions)
        {
            if (containers.TryGetValue(containerRevision.ContainerId, out var container))
            {
                container.Revision = containerRevision.Revision;
            }
        }

        CharacterRevision = result.CarryState.ItemStateRevision;
        return true;
    }

    private void Add(FullStackItemContainerResponse source)
    {
        containers[source.ContainerId] = new ContainerState(
            source.ContainerId,
            source.Revision,
            source.Slots.ToDictionary(
                slot => slot.SlotIndex,
                slot => new SlotState(slot.SlotIndex, slot.Item)));
    }

    public sealed record OwnedItem(
        Guid ContainerId,
        long ContainerRevision,
        int SlotIndex,
        FullStackItemInstanceResponse Item);

    private sealed class ContainerState(
        Guid containerId,
        long revision,
        Dictionary<int, SlotState> slots)
    {
        public Guid ContainerId { get; } = containerId;

        public long Revision { get; set; } = revision;

        public Dictionary<int, SlotState> Slots { get; } = slots;
    }

    private sealed record SlotState(int SlotIndex, FullStackItemInstanceResponse? Item);
}
