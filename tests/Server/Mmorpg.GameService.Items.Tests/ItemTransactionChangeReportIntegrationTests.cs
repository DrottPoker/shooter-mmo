using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// A committed live item operation reports the final state of every item it changed and the
// character's balance, so the client can patch its inventory to exactly the snapshot that a
// reload would return.
public sealed class ItemTransactionChangeReportIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task EveryMoveReportsExactlyTheItemsTheSnapshotChanged()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "change-report@example.com",
            "change_report_player",
            "Change Report Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var inventory = snapshot.PermanentInventory.ContainerId;
        var bank = snapshot.Bank.ContainerId;
        foreach (var (definitionId, quantity, slotIndex) in new[]
                 {
                     ("fixture.ore", 10, 0),
                     ("fixture.sword", 1, 1),
                     ("fixture.bow", 1, 2)
                 })
        {
            var grant = await context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(
                        player.Character.Id,
                        snapshot.ItemStateRevision,
                        definitionId,
                        quantity,
                        inventory,
                        slotIndex)),
                CancellationToken.None);
            Assert.True(grant.Succeeded, grant.Error?.Message);
            snapshot = await GetSnapshotAsync(context, player);
        }

        var session = await JoinAsync(context, player);
        var ore = Item(snapshot.PermanentInventory, 0);
        var sword = Item(snapshot.PermanentInventory, 1);

        // Moving into the Bank changes one item and the carried weight.
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Relocate,
            snapshot,
            sword) with
        { DestinationContainerId = bank, DestinationSlotIndex = 3 });

        // A split creates a stack the client has never seen.
        ore = Item(snapshot.PermanentInventory, 0);
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.SplitStack,
            snapshot,
            ore) with
        { Quantity = 4, DestinationContainerId = inventory, DestinationSlotIndex = 5 });

        // A merge removes its source.
        ore = Item(snapshot.PermanentInventory, 0);
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.MergeStacks,
            snapshot,
            Item(snapshot.PermanentInventory, 5),
            ore));

        // A swap between carried storage and the Bank moves both items.
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.SwapContainerItems,
            snapshot,
            Item(snapshot.PermanentInventory, 0),
            Item(snapshot.Bank, 3)));

        // Equipping onto an occupied slot swaps the equipped item into the source slot.
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Equip,
            snapshot,
            Item(snapshot.PermanentInventory, 0)) with
        { EquipmentSlotId = "main_hand" });
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Equip,
            snapshot,
            Item(snapshot.PermanentInventory, 2)) with
        { EquipmentSlotId = "main_hand" });

        // Unequipping and destroying finish the round.
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Unequip,
            snapshot,
            Equipped(snapshot, "main_hand")) with
        { DestinationContainerId = inventory, DestinationSlotIndex = 7 });
        await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Destroy,
            snapshot,
            Item(snapshot.PermanentInventory, 7)));
    }

    [PostgresIntegrationFact]
    public async Task BagEquipsAndRecoveryClaimsReportTheirContentsAndDeliveries()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "bag-claim-report@example.com",
            "bag_claim_report_player",
            "Bag Claim Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var inventory = snapshot.PermanentInventory.ContainerId;
        foreach (var (definitionId, quantity, slotIndex) in new[]
                 {
                     ("fixture.pack", 1, 0),
                     ("fixture.ore", 5, 1),
                     ("fixture.potion", 2, 2)
                 })
        {
            var grant = await context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(
                        player.Character.Id,
                        snapshot.ItemStateRevision,
                        definitionId,
                        quantity,
                        inventory,
                        slotIndex)),
                CancellationToken.None);
            Assert.True(grant.Succeeded, grant.Error?.Message);
            snapshot = await GetSnapshotAsync(context, player);
        }

        var potion = Item(snapshot.PermanentInventory, 2);
        var delivery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "change_report_test",
                    Guid.NewGuid().ToString("N"),
                    DateTime.UtcNow.AddMinutes(-1),
                    null,
                    [new ItemRevisionExpectation(potion.ItemInstanceId, potion.Revision)])),
            CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Error?.Message);
        snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);

        // An equipped Bag names its contents, which the client shows from then on.
        var pack = Item(snapshot.PermanentInventory, 0);
        var equipped = await ReportAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Equip,
            snapshot,
            pack) with
        { EquipmentSlotId = "bag" });
        snapshot = equipped.After;
        var reportedPack = equipped.Result.ItemChanges!.Single(change => change.ItemInstanceId == pack.ItemInstanceId);
        Assert.Equal(snapshot.EquippedBag!.Contents.ContainerId, reportedPack.BagContentsContainerId);
        Assert.Equal(snapshot.EquippedBag.Contents.Revision, reportedPack.BagContentsRevision);

        // Items move into and out of the Bag like any container.
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Relocate,
            snapshot,
            Item(snapshot.PermanentInventory, 1)) with
        { DestinationContainerId = snapshot.EquippedBag.Contents.ContainerId, DestinationSlotIndex = 0 });

        // A claim reports the delivery it took out of Recovery Storage.
        var recovery = Assert.Single(snapshot.RecoveryStorage.Deliveries);
        var claim = Request(session, ItemOperationKinds.ClaimRecoveryDelivery, snapshot, null) with
        {
            RecoveryDeliveryId = recovery.DeliveryId,
            ExpectedRecoveryDeliveryRevision = recovery.Revision,
            DestinationContainerId = inventory,
            Items = recovery.Items
                .Select(item => new ItemRevisionExpectation(item.Item.ItemInstanceId, item.Item.Revision))
                .ToArray()
        };
        snapshot = await AssertReportedAsync(context, player, session, snapshot, claim);
        Assert.Empty(snapshot.RecoveryStorage.Deliveries);

        // An emptied Bag unequips, and its contents leave view.
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Relocate,
            snapshot,
            Item(snapshot.EquippedBag!.Contents, 0)) with
        { DestinationContainerId = inventory, DestinationSlotIndex = 6 });
        snapshot = await AssertReportedAsync(context, player, session, snapshot, Request(
            session,
            ItemOperationKinds.Unequip,
            snapshot,
            Equipped(snapshot, "bag")) with
        { DestinationContainerId = inventory, DestinationSlotIndex = 7 });
        Assert.Null(snapshot.EquippedBag);
    }

    private static async Task<CharacterInventorySnapshotResponse> AssertReportedAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        ConsumedSimulationJoinTicketResponse session,
        CharacterInventorySnapshotResponse before,
        SimulationItemOperationRequest request)
    {
        return (await ReportAsync(context, player, session, before, request)).After;
    }

    private static async Task<Reported> ReportAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        ConsumedSimulationJoinTicketResponse session,
        CharacterInventorySnapshotResponse before,
        SimulationItemOperationRequest request)
    {
        var executed = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        Assert.True(executed.Succeeded, executed.Error?.Message);
        var result = executed.Value!;
        var after = await GetSnapshotAsync(context, player);
        var character = Assert.Single(result.CharacterRevisions);
        Assert.Equal(after.ItemStateRevision, character.Revision);
        Assert.Equal(before.ItemStateRevision + 1, character.Revision);
        Assert.Equal(after.Currency, character.Currency);
        Assert.NotNull(result.ItemChanges);

        var beforeItems = Flatten(before);
        var afterItems = Flatten(after);
        var changedInSnapshot = beforeItems.Keys
            .Union(afterItems.Keys)
            .Where(itemId => !beforeItems.TryGetValue(itemId, out var old)
                || !afterItems.TryGetValue(itemId, out var current)
                || old != current)
            .Order()
            .ToArray();
        Assert.Equal(changedInSnapshot, result.ItemChanges!.Select(change => change.ItemInstanceId).Order());
        foreach (var change in result.ItemChanges!)
        {
            if (change.Removed)
            {
                Assert.False(afterItems.ContainsKey(change.ItemInstanceId));
                continue;
            }

            var reported = new SnapshotItem(
                change.EquipmentSlotId is null
                    ? change.ContainerId + ":" + change.ContainerSlotIndex
                    : "equipment:" + change.EquipmentSlotId,
                change.DefinitionId!,
                change.Quantity,
                change.Revision,
                string.Join(",", change.Policies.Select(policy => policy.PolicyKind + "/" + policy.ProtectionSource)));
            Assert.Equal(afterItems[change.ItemInstanceId], reported);
        }

        // Every container whose contents changed reports the revision the snapshot shows.
        foreach (var container in new[] { after.PermanentInventory, after.Bank })
        {
            var beforeContainer = container.ContainerId == before.PermanentInventory.ContainerId
                ? before.PermanentInventory
                : before.Bank;
            var revision = result.ContainerRevisions.SingleOrDefault(value => value.ContainerId == container.ContainerId);
            if (revision is null)
            {
                Assert.Equal(beforeContainer.Revision, container.Revision);
            }
            else
            {
                Assert.Equal(container.Revision, revision.Revision);
            }
        }

        return new Reported(after, result);
    }


    private static SimulationItemOperationRequest Request(
        ConsumedSimulationJoinTicketResponse session,
        string operationKind,
        CharacterInventorySnapshotResponse snapshot,
        ItemInstanceSnapshotResponse? item,
        ItemInstanceSnapshotResponse? target = null)
    {
        return new SimulationItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(true, true, false),
            operationKind,
            snapshot.ItemStateRevision,
            item?.ItemInstanceId,
            item?.Revision,
            target?.ItemInstanceId,
            target?.Revision);
    }

    private static Dictionary<Guid, SnapshotItem> Flatten(CharacterInventorySnapshotResponse snapshot)
    {
        var items = new Dictionary<Guid, SnapshotItem>();
        foreach (var container in new[] { snapshot.PermanentInventory, snapshot.Bank, snapshot.SecureContainer.Contents, snapshot.EquippedBag?.Contents }.OfType<ItemContainerSnapshotResponse>())
        {
            foreach (var slot in container.Slots.Where(slot => slot.Item is not null))
            {
                items.Add(slot.Item!.ItemInstanceId, ToSnapshotItem(container.ContainerId + ":" + slot.SlotIndex, slot.Item));
            }
        }

        foreach (var slot in snapshot.Equipment.Where(slot => slot.Item is not null))
        {
            items.Add(slot.Item!.ItemInstanceId, ToSnapshotItem("equipment:" + slot.EquipmentSlotId, slot.Item));
        }

        return items;
    }

    private static SnapshotItem ToSnapshotItem(string location, ItemInstanceSnapshotResponse item)
    {
        return new SnapshotItem(
            location,
            item.DefinitionId,
            item.Quantity,
            item.Revision,
            string.Join(",", item.Policies.Select(policy => policy.PolicyKind + "/" + policy.ProtectionSource)));
    }

    private static ItemInstanceSnapshotResponse Item(ItemContainerSnapshotResponse container, int slotIndex)
    {
        return container.Slots.Single(slot => slot.SlotIndex == slotIndex).Item!;
    }

    private static ItemInstanceSnapshotResponse Equipped(CharacterInventorySnapshotResponse snapshot, string slotId)
    {
        return snapshot.Equipment.Single(slot => slot.EquipmentSlotId == slotId).Item!;
    }

    private static async Task<CharacterInventorySnapshotResponse> GetSnapshotAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                WorkerId,
                RuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private sealed record Reported(CharacterInventorySnapshotResponse After, SimulationItemOperationResponse Result);

    private sealed record SnapshotItem(
        string Location,
        string DefinitionId,
        int Quantity,
        long Revision,
        string Policies);
}
