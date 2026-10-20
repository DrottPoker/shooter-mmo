using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Items;

// Turns a committed transaction of the owner into the change the client patches its
// inventory with, named by the client operation that caused it, if any. It returns null
// unless the transaction lists its whole change and that change fits one packet, and the
// client then reloads its inventory instead.
internal static class OwnerItemStateChangeMapper
{
    public static RealtimeOwnerItemStateChanged? TryCreate(
        Guid operationId,
        Guid characterId,
        SimulationItemTransactionResponse transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Recovery deliveries and Secure Container tiers are state the change cannot describe.
        if (!transaction.Succeeded
            || transaction.ItemChanges is null
            || transaction.RecoveryDeliveryIds is not { Count: 0 }
            || transaction.SecureContainerEntitlementRevision is not null
            || transaction.ContainerRevisions is null
            || transaction.CharacterRevisions is not { Count: 1 }
            || transaction.CharacterRevisions[0].CharacterId != characterId
            || transaction.CharacterRevisions[0].Currency is not { } currency
            || transaction.CharacterRevisions[0].Revision < 1)
        {
            return null;
        }

        var items = new RealtimeItemStateChange[transaction.ItemChanges.Count];
        for (var index = 0; index < items.Length; index++)
        {
            var item = TryMapItem(characterId, transaction.ItemChanges[index]);
            if (item is null)
            {
                return null;
            }

            items[index] = item;
        }

        var character = transaction.CharacterRevisions[0];
        var change = new RealtimeOwnerItemStateChanged(
            operationId,
            character.Revision - 1,
            new RealtimeCarryState(character.Revision, character.CarriedWeight, character.CarryCapacity),
            currency,
            transaction.ContainerRevisions
                .Select(revision => new RealtimeContainerRevision(revision.ContainerId, revision.Revision))
                .ToArray(),
            items);
        return RealtimeProtocol.CanEncodeOwnerItemStateChanged(change) ? change : null;
    }

    private static RealtimeItemStateChange? TryMapItem(Guid characterId, SimulationItemChange item)
    {
        if (item.Removed)
        {
            return new RealtimeItemStateChange(
                item.ItemInstanceId,
                RealtimeItemStateLocationKind.Removed,
                string.Empty,
                0,
                0,
                Guid.Empty,
                -1,
                string.Empty,
                []);
        }

        var policies = (item.Policies ?? [])
            .Select(policy => new RealtimeItemPolicySummary(
                policy.PolicyKind,
                policy.Status,
                policy.ProtectionSource))
            .ToArray();
        if (item.ContainerId is { } containerId && item.ContainerSlotIndex is { } slotIndex)
        {
            return new RealtimeItemStateChange(
                item.ItemInstanceId,
                RealtimeItemStateLocationKind.Container,
                item.DefinitionId ?? string.Empty,
                item.Quantity,
                item.Revision,
                containerId,
                slotIndex,
                string.Empty,
                policies);
        }

        // An item equipped by anyone else is not the owner's to show.
        return item.EquippedCharacterId == characterId && !string.IsNullOrWhiteSpace(item.EquipmentSlotId)
            ? new RealtimeItemStateChange(
                item.ItemInstanceId,
                RealtimeItemStateLocationKind.Equipment,
                item.DefinitionId ?? string.Empty,
                item.Quantity,
                item.Revision,
                Guid.Empty,
                -1,
                item.EquipmentSlotId,
                policies)
            : null;
    }
}
