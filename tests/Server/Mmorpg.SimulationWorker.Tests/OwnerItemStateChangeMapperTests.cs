using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Tests;

// The worker sends an item state change only when the transaction lists its whole change in
// one packet; every other committed transaction leaves the client to reload its inventory.
public sealed class OwnerItemStateChangeMapperTests
{
    private static readonly Guid OperationId = Guid.NewGuid();
    private static readonly Guid CharacterId = Guid.NewGuid();
    private static readonly Guid ContainerId = Guid.NewGuid();

    [Fact]
    public void MapsContainedEquippedAndRemovedItems()
    {
        var contained = Guid.NewGuid();
        var equipped = Guid.NewGuid();
        var removed = Guid.NewGuid();

        var change = OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction(
                [
                    Contained(contained),
                    new SimulationItemChange(equipped, false, "sword", 1, 3, null, null, CharacterId, "main_hand", null),
                    new SimulationItemChange(removed, true, null, 0, 0, null, null, null, null, null)
                ]));

        Assert.NotNull(change);
        Assert.Equal(OperationId, change.OperationId);
        Assert.Equal(6, change.PreviousItemStateRevision);
        Assert.Equal(7, change.CarryState.ItemStateRevision);
        Assert.Equal(90, change.Currency);
        Assert.Equal(new[] { ContainerId }, change.ContainerRevisions.Select(revision => revision.ContainerId));
        Assert.Collection(
            change.Items,
            item =>
            {
                Assert.Equal(RealtimeItemStateLocationKind.Container, item.Location);
                Assert.Equal(ContainerId, item.ContainerId);
                Assert.Equal(1, item.SlotIndex);
            },
            item =>
            {
                Assert.Equal(RealtimeItemStateLocationKind.Equipment, item.Location);
                Assert.Equal("main_hand", item.EquipmentSlotId);
                Assert.Equal(-1, item.SlotIndex);
            },
            item =>
            {
                Assert.Equal(RealtimeItemStateLocationKind.Removed, item.Location);
                Assert.Empty(item.DefinitionId);
            });
    }

    [Fact]
    public void ClaimsNameTheirDeliveriesAndEquippedBagsTheirContents()
    {
        var deliveryId = Guid.NewGuid();
        var bagId = Guid.NewGuid();
        var contentsId = Guid.NewGuid();

        var change = OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction(
                [
                    Contained(Guid.NewGuid()),
                    new SimulationItemChange(bagId, false, "pack", 1, 2, null, null, CharacterId, "bag", null, contentsId, 4)
                ]) with
            {
                OperationKind = "claim_recovery_delivery",
                RecoveryDeliveryIds = [deliveryId]
            });

        Assert.NotNull(change);
        Assert.Equal(new[] { deliveryId }, change.ClaimedRecoveryDeliveryIds);
        var bag = change.Items.Single(item => item.ItemInstanceId == bagId);
        Assert.Equal(contentsId, bag.BagContentsContainerId);
        Assert.Equal(4, bag.BagContentsRevision);
        Assert.All(
            change.Items.Where(item => item.ItemInstanceId != bagId),
            item => Assert.Equal(Guid.Empty, item.BagContentsContainerId));
    }

    [Fact]
    public void TransactionsThatDoNotDescribeTheWholeChangeSendNothing()
    {
        // GameService listed no changes, because the transaction changed too many items.
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(OperationId, CharacterId, Transaction(null)));
        // A result stored before balances were reported has no currency.
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction([Contained(Guid.NewGuid())], currency: null)));
        // Recovery deliveries are state the change cannot describe.
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction([Contained(Guid.NewGuid())]) with { RecoveryDeliveryIds = [Guid.NewGuid()] }));
        // Another character's transaction, or an item another character equips.
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(OperationId, Guid.NewGuid(), Transaction([Contained(Guid.NewGuid())])));
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction(
                [new SimulationItemChange(Guid.NewGuid(), false, "sword", 1, 3, null, null, Guid.NewGuid(), "main_hand", null)])));
        // More items than one change carries.
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction(Enumerable.Range(0, 17).Select(_ => Contained(Guid.NewGuid())).ToArray())));
        // Items that do not fit one packet.
        Assert.Null(OwnerItemStateChangeMapper.TryCreate(
            OperationId,
            CharacterId,
            Transaction(Enumerable.Range(0, 8)
                .Select(index => Contained(Guid.NewGuid()) with { DefinitionId = new string('d', 128) })
                .ToArray())));
    }

    private static SimulationItemChange Contained(Guid itemInstanceId)
    {
        return new SimulationItemChange(
            itemInstanceId,
            false,
            "potion",
            2,
            5,
            ContainerId,
            1,
            null,
            null,
            [new SimulationItemPolicySummary("insured", "active", "insurance_npc")]);
    }

    private static SimulationItemTransactionResponse Transaction(
        IReadOnlyList<SimulationItemChange>? items,
        long? currency = 90)
    {
        return new SimulationItemTransactionResponse(
            Guid.NewGuid(),
            "relocate",
            true,
            null,
            [new SimulationItemCharacterRevision(CharacterId, 7, 40, 200, currency)],
            [new SimulationItemContainerRevision(ContainerId, 3)],
            [],
            [],
            null,
            items);
    }
}
