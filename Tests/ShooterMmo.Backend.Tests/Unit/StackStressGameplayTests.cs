using ShooterMmo.GameProtocol;
using ShooterMmo.Tools.StackStressGenerator;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class StackStressGameplayTests
{
    [Fact]
    public void InventoryStateAppliesAuthoritativeRelocateResult()
    {
        var itemId = Guid.NewGuid();
        var permanentId = Guid.NewGuid();
        var secureId = Guid.NewGuid();
        var state = new StressInventoryState(CreateInventory(
            itemId,
            permanentId,
            secureId));
        var intent = RealtimeItemOperationIntent.CreateRelocate(
            Guid.NewGuid(),
            1,
            itemId,
            1,
            secureId,
            0);
        var result = new RealtimeItemOperationResult(
            intent.OperationId,
            intent.OperationKind,
            true,
            false,
            null!,
            new RealtimeCarryState(2, 2, 200),
            [new RealtimeItemRevision(itemId, 2)],
            [
                new RealtimeContainerRevision(permanentId, 2),
                new RealtimeContainerRevision(secureId, 2)
            ],
            []);

        Assert.True(state.ApplyRelocate(intent, result));
        var moved = state.FindItem("medical.field_dressing");
        Assert.NotNull(moved);
        Assert.Equal(secureId, moved.ContainerId);
        Assert.Equal(2, moved.Item.Revision);
        Assert.Equal(2, state.CharacterRevision);
    }

    [Fact]
    public void CorpseViewAppliesCompleteSnapshotAndDelta()
    {
        var corpseId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var state = new StressCorpseViewState();
        var snapshotId = Guid.NewGuid();
        var snapshot = new RealtimeCorpseViewStateChunk(
            snapshotId,
            RealtimeCorpseViewUpdateKind.Snapshot,
            corpseId,
            -1,
            1,
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            "Stress Corpse",
            "corpse.generic_loot_crate",
            0,
            1,
            "general_inventory",
            containerId,
            1,
            true,
            1,
            0,
            [new RealtimeCorpseSlot(
                0,
                "general",
                [],
                new RealtimeCorpseItem(
                    itemId,
                    "material.iron_ore",
                    50,
                    1,
                    Guid.Empty,
                    0))]);

        Assert.Equal(CorpseViewApplyResult.Applied, state.Apply(snapshot));
        Assert.Equal(50, state.FindLootStack("material.iron_ore", false)!.Item.Quantity);

        var delta = new RealtimeCorpseViewStateChunk(
            Guid.NewGuid(),
            RealtimeCorpseViewUpdateKind.Delta,
            corpseId,
            1,
            2,
            snapshot.ExpiresAtUnixMilliseconds,
            snapshot.SourceDisplayName,
            snapshot.PresentationKey,
            0,
            1,
            "general_inventory",
            containerId,
            2,
            false,
            1,
            0,
            [new RealtimeCorpseSlot(
                0,
                "general",
                [],
                new RealtimeCorpseItem(
                    itemId,
                    "material.iron_ore",
                    49,
                    2,
                    Guid.Empty,
                    0))]);

        Assert.Equal(CorpseViewApplyResult.Applied, state.Apply(delta));
        Assert.Equal(2, state.Revision);
        Assert.Equal(49, state.FindLootStack("material.iron_ore", true)!.Item.Quantity);
    }

    [Fact]
    public void GameplaySummaryTreatsUnansweredRequestsAsFailures()
    {
        var metrics = new StressGameplayMetrics(1337);
        metrics.RecordRequest("corpse_loot_partial");

        var summary = metrics.Capture(StressWorkloadProfile.LootHotspot, Guid.NewGuid());

        Assert.Equal(1, summary.UnexpectedFailures);
        Assert.Equal(1, summary.Operations["corpse_loot_partial"].IncompleteRequests);
    }

    private static FullStackCharacterInventoryResponse CreateInventory(
        Guid itemId,
        Guid permanentId,
        Guid secureId)
    {
        return new FullStackCharacterInventoryResponse(
            Guid.NewGuid(),
            "catalog",
            1,
            new FullStackItemContainerResponse(
                permanentId,
                "permanent_inventory",
                1,
                2,
                [
                    new FullStackItemSlotResponse(
                        0,
                        "general",
                        [],
                        new FullStackItemInstanceResponse(
                            itemId,
                            "medical.field_dressing",
                            1,
                            1)),
                    new FullStackItemSlotResponse(1, "general", [], null)
                ]),
            [],
            null,
            new FullStackItemContainerResponse(
                Guid.NewGuid(),
                "bank",
                0,
                1,
                [new FullStackItemSlotResponse(0, "general", [], null)]),
            new FullStackSecureContainerResponse(
                "base",
                1,
                new FullStackItemContainerResponse(
                    secureId,
                    "secure_container",
                    1,
                    1,
                    [new FullStackItemSlotResponse(0, "general", [], null)])),
            new FullStackRecoveryStorageResponse(Guid.NewGuid(), 0, []),
            2,
            200,
            100,
            true,
            10_000);
    }
}
