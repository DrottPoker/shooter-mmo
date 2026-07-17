using ShooterMmo.GameProtocol;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeCorpseProtocolTests
{
    [Fact]
    public void CorpseInteractionIntentsRoundTripEveryOperationShape()
    {
        var corpseId = Guid.NewGuid();
        var intents = new[]
        {
            RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId),
            RealtimeCorpseInteractionIntent.CreateClose(Guid.NewGuid(), corpseId, 7),
            RealtimeCorpseInteractionIntent.CreateRefresh(Guid.NewGuid(), corpseId, 8),
            RealtimeCorpseInteractionIntent.CreateLootItem(
                Guid.NewGuid(),
                corpseId,
                9,
                Guid.NewGuid(),
                3,
                Guid.NewGuid(),
                5,
                2,
                Guid.NewGuid(),
                4),
            RealtimeCorpseInteractionIntent.CreateLootPartialStack(
                Guid.NewGuid(),
                corpseId,
                10,
                Guid.NewGuid(),
                6,
                2,
                Guid.NewGuid(),
                7,
                3,
                Guid.Empty,
                0),
            RealtimeCorpseInteractionIntent.CreateDepositItem(
                Guid.NewGuid(),
                corpseId,
                10,
                Guid.NewGuid(),
                6,
                Guid.NewGuid(),
                7,
                3,
                Guid.NewGuid(),
                8),
            RealtimeCorpseInteractionIntent.CreateDepositPartialStack(
                Guid.NewGuid(),
                corpseId,
                10,
                Guid.NewGuid(),
                6,
                2,
                Guid.NewGuid(),
                7,
                3,
                Guid.Empty,
                0),
            RealtimeCorpseInteractionIntent.CreateSwapBag(
                Guid.NewGuid(),
                corpseId,
                11,
                Guid.NewGuid(),
                8,
                Guid.NewGuid(),
                9,
                Guid.NewGuid(),
                10,
                Guid.NewGuid(),
                11)
        };

        foreach (var expected in intents)
        {
            var decoded = RealtimeProtocol.TryDecodeCorpseInteractionIntent(
                RealtimeProtocol.EncodeCorpseInteractionIntent(expected),
                out var actual,
                out var error);

            Assert.True(decoded, error);
            Assert.Equal(expected.OperationId, actual.OperationId);
            Assert.Equal(expected.OperationKind, actual.OperationKind);
            Assert.Equal(expected.CorpseId, actual.CorpseId);
            Assert.Equal(expected.ExpectedCorpseRevision, actual.ExpectedCorpseRevision);
            Assert.Equal(expected.ItemInstanceId, actual.ItemInstanceId);
            Assert.Equal(expected.ExpectedItemRevision, actual.ExpectedItemRevision);
            Assert.Equal(expected.Quantity, actual.Quantity);
            Assert.Equal(expected.DestinationContainerId, actual.DestinationContainerId);
            Assert.Equal(
                expected.ExpectedDestinationContainerRevision,
                actual.ExpectedDestinationContainerRevision);
            Assert.Equal(expected.DestinationSlotIndex, actual.DestinationSlotIndex);
            Assert.Equal(expected.TargetItemInstanceId, actual.TargetItemInstanceId);
            Assert.Equal(expected.ExpectedTargetItemRevision, actual.ExpectedTargetItemRevision);
            Assert.Equal(
                expected.CorpseBagContentsContainerId,
                actual.CorpseBagContentsContainerId);
            Assert.Equal(
                expected.ExpectedCorpseBagContentsRevision,
                actual.ExpectedCorpseBagContentsRevision);
            Assert.Equal(expected.PlayerBagItemInstanceId, actual.PlayerBagItemInstanceId);
            Assert.Equal(expected.ExpectedPlayerBagRevision, actual.ExpectedPlayerBagRevision);
            Assert.Equal(
                expected.PlayerBagContentsContainerId,
                actual.PlayerBagContentsContainerId);
            Assert.Equal(
                expected.ExpectedPlayerBagContentsRevision,
                actual.ExpectedPlayerBagContentsRevision);
        }
    }

    [Fact]
    public void CorpsePresenceChunksRoundTripWithinPacketLimit()
    {
        var expected = new RealtimeCorpsePresenceSnapshotChunk(
            42,
            0,
            1,
            Enumerable.Range(0, 3)
                .Select(index => new RealtimeCorpsePresence(
                    Guid.NewGuid(),
                    index,
                    1,
                    -index,
                    DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
                    index == 2,
                    "Corpse " + index,
                    "player_corpse"))
                .ToArray());

        var packet = RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(expected);
        Assert.True(packet.Length <= RealtimeProtocol.MaximumPacketSize);
        Assert.True(
            RealtimeProtocol.TryDecodeCorpsePresenceSnapshotChunk(
                packet,
                out var actual,
                out var error),
            error);
        Assert.Equal(expected.SnapshotSequence, actual.SnapshotSequence);
        Assert.Equal(expected.Corpses.Select(corpse => corpse.CorpseId),
            actual.Corpses.Select(corpse => corpse.CorpseId));
    }

    [Fact]
    public void CorpseInteractionResultsRoundTripSuccessAndStableFailure()
    {
        var carry = new RealtimeCarryState(12, 150, 250);
        var success = new RealtimeCorpseInteractionResult(
            Guid.NewGuid(),
            RealtimeCorpseInteractionKind.LootItem,
            Guid.NewGuid(),
            true,
            false,
            true,
            null!,
            carry);
        AssertRoundTrip(success);

        var failure = new RealtimeCorpseInteractionResult(
            Guid.NewGuid(),
            RealtimeCorpseInteractionKind.LootPartialStack,
            Guid.NewGuid(),
            false,
            true,
            true,
            new RealtimeError(
                "item_quantity_changed",
                "The corpse stack changed."),
            carry);
        var actual = AssertRoundTrip(failure);
        Assert.Equal("item_quantity_changed", actual.Error.Code);
    }

    [Fact]
    public void CorpseViewStateRoundTripsEmptyAndOccupiedSlots()
    {
        var expected = new RealtimeCorpseViewStateChunk(
            Guid.NewGuid(),
            RealtimeCorpseViewUpdateKind.Delta,
            Guid.NewGuid(),
            3,
            4,
            DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeMilliseconds(),
            "Fallen Hero",
            "player_corpse",
            0,
            1,
            "general_inventory",
            Guid.NewGuid(),
            7,
            false,
            2,
            0,
            [
                new RealtimeCorpseSlot(0, "general", ["medical"], null!),
                new RealtimeCorpseSlot(
                    1,
                    "general",
                    new RealtimeCorpseItem(
                        Guid.NewGuid(),
                        "material.iron_ore",
                        4,
                        2,
                        Guid.Empty,
                        0))
            ]);

        var packet = RealtimeProtocol.EncodeCorpseViewStateChunk(expected);
        Assert.True(packet.Length <= RealtimeProtocol.MaximumPacketSize);
        Assert.True(
            RealtimeProtocol.TryDecodeCorpseViewStateChunk(
                packet,
                out var actual,
                out var error),
            error);
        Assert.Equal(expected.UpdateId, actual.UpdateId);
        Assert.Equal(expected.BaseRevision, actual.BaseRevision);
        Assert.Null(actual.Slots[0].Item);
        Assert.Equal(["medical"], actual.Slots[0].AcceptedTags);
        Assert.Equal("material.iron_ore", actual.Slots[1].Item.DefinitionId);
    }

    [Fact]
    public void CorpseViewClosureRoundTripsStableReason()
    {
        var expected = new RealtimeCorpseViewClosed(
            Guid.NewGuid(),
            "corpse_expired",
            "The corpse expired.");

        Assert.True(
            RealtimeProtocol.TryDecodeCorpseViewClosed(
                RealtimeProtocol.EncodeCorpseViewClosed(expected),
                out var actual,
                out var error),
            error);
        Assert.Equal(expected.CorpseId, actual.CorpseId);
        Assert.Equal(expected.Code, actual.Code);
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void InvalidCorpseIntentShapeIsRejectedBeforeEncoding()
    {
        Assert.Throws<ArgumentException>(() =>
            RealtimeProtocol.EncodeCorpseInteractionIntent(
                RealtimeCorpseInteractionIntent.CreateOpen(
                    Guid.Empty,
                    Guid.NewGuid())));
    }

    private static RealtimeCorpseInteractionResult AssertRoundTrip(
        RealtimeCorpseInteractionResult expected)
    {
        Assert.True(
            RealtimeProtocol.TryDecodeCorpseInteractionResult(
                RealtimeProtocol.EncodeCorpseInteractionResult(expected),
                out var actual,
                out var error),
            error);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.OperationKind, actual.OperationKind);
        Assert.Equal(expected.CorpseId, actual.CorpseId);
        Assert.Equal(expected.Succeeded, actual.Succeeded);
        Assert.Equal(expected.RequiresCorpseRefresh, actual.RequiresCorpseRefresh);
        Assert.Equal(expected.RequiresInventoryRefresh, actual.RequiresInventoryRefresh);
        Assert.Equal(expected.CarryState.ItemStateRevision, actual.CarryState.ItemStateRevision);
        return actual;
    }
}
