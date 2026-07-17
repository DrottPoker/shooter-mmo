using ShooterMmo.GameProtocol;
using SimulationWorker.Auth;
using SimulationWorker.Corpses;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class CorpseRealtimeStateTests
{
    [Fact]
    public void ViewerRegistryAllowsSharedCorpseButOnlyOnePerPeer()
    {
        var registry = new CorpseViewerRegistry();
        var corpse = Guid.NewGuid();
        var otherCorpse = Guid.NewGuid();

        Assert.True(registry.TryOpen(1, corpse, out _, out _));
        Assert.True(registry.TryOpen(2, corpse, out _, out _));
        Assert.True(registry.TryOpen(1, corpse, out _, out _));
        Assert.False(registry.TryOpen(1, otherCorpse, out var code, out _));
        Assert.Equal("corpse_interaction_active", code);
        Assert.Equal(new[] { 1, 2 }, registry.GetViewers(corpse));

        Assert.True(registry.Close(1, corpse));
        Assert.False(registry.IsViewing(1, corpse));
        Assert.True(registry.IsViewing(2, corpse));
        Assert.Equal(new[] { 2 }, registry.CloseCorpse(corpse));
        Assert.Empty(registry.GetViewers(corpse));
    }

    [Fact]
    public void TargetedDeltaCarriesOnlyChangedSlotsAndRevision()
    {
        var corpseId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var previous = CreateSnapshot(
            corpseId,
            4,
            containerId,
            7,
            itemId,
            5,
            6);
        var current = CreateSnapshot(
            corpseId,
            5,
            containerId,
            8,
            itemId,
            6,
            4);

        var chunks = CorpseRealtimePacketBuilder.BuildDelta(previous, current);

        var chunk = Assert.Single(chunks);
        Assert.Equal(RealtimeCorpseViewUpdateKind.Delta, chunk.UpdateKind);
        Assert.Equal(4, chunk.BaseRevision);
        Assert.Equal(5, chunk.Revision);
        Assert.False(chunk.ReplaceSection);
        var changedSlot = Assert.Single(chunk.Slots);
        Assert.Equal(0, changedSlot.SlotIndex);
        Assert.Equal(4, changedSlot.Item.Quantity);
        Assert.True(
            RealtimeProtocol.EncodeCorpseViewStateChunk(chunk).Length
                <= RealtimeProtocol.MaximumPacketSize);
    }

    [Fact]
    public void BagContainerIdentityChangeReplacesWholeSection()
    {
        var corpseId = Guid.NewGuid();
        var previous = CreateSnapshot(
            corpseId,
            9,
            Guid.NewGuid(),
            3,
            Guid.NewGuid(),
            1,
            2,
            "bag");
        var current = CreateSnapshot(
            corpseId,
            10,
            Guid.NewGuid(),
            4,
            Guid.NewGuid(),
            1,
            3,
            "bag");

        var chunks = CorpseRealtimePacketBuilder.BuildDelta(previous, current);

        Assert.All(chunks, chunk => Assert.True(chunk.ReplaceSection));
        Assert.Equal(3, chunks.Sum(chunk => chunk.Slots.Length));
    }

    [Fact]
    public void PresenceBuilderKeepsUnicodeNamesInsideMtu()
    {
        var now = DateTime.UtcNow;
        var corpses = Enumerable.Range(0, 8)
            .Select(index => new DurableCorpseState(
                Guid.NewGuid(),
                null,
                new string('Ö', 128),
                "local-city-1",
                index,
                0,
                0,
                0,
                0,
                0,
                1,
                "player_corpse",
                index,
                now,
                now.AddMinutes(5),
                false,
                Array.Empty<CorpseSectionState>()))
            .ToArray();

        var chunks = CorpseRealtimePacketBuilder.BuildPresence(3, corpses);

        Assert.Equal(8, chunks.Sum(chunk => chunk.Corpses.Length));
        Assert.All(chunks, chunk => Assert.True(
            RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(chunk).Length
                <= RealtimeProtocol.MaximumPacketSize));
    }

    [Fact]
    public void ViewBuilderDynamicallyPacksUnicodeStateInsideMtu()
    {
        var snapshot = CreateSnapshot(
            Guid.NewGuid(),
            12,
            Guid.NewGuid(),
            8,
            Guid.NewGuid(),
            3,
            2,
            slotCapacity: 8,
            fillEverySlot: true,
            definitionId: new string('d', 128)) with
        {
            SourceDisplayName = new string('\u754c', 128),
            PresentationKey = new string('p', 128)
        };

        var chunks = CorpseRealtimePacketBuilder.BuildSnapshot(snapshot);

        Assert.True(chunks.Count > 2);
        Assert.Equal(8, chunks.Sum(chunk => chunk.Slots.Length));
        Assert.All(chunks, chunk => Assert.True(
            RealtimeProtocol.EncodeCorpseViewStateChunk(chunk).Length
                <= RealtimeProtocol.MaximumPacketSize));
    }

    private static CorpseViewSnapshotResponse CreateSnapshot(
        Guid corpseId,
        long revision,
        Guid containerId,
        long containerRevision,
        Guid itemId,
        long itemRevision,
        int quantity,
        string sectionKind = "general_inventory",
        int slotCapacity = 3,
        bool fillEverySlot = false,
        string definitionId = "material.iron_ore")
    {
        var slots = Enumerable.Range(0, slotCapacity)
            .Select(index => new CorpseViewSlotResponse(
                index,
                "general",
                Array.Empty<string>(),
                index == 0 || fillEverySlot
                    ? new CorpseViewItemResponse(
                        index == 0 ? itemId : Guid.NewGuid(),
                        definitionId,
                        quantity,
                        itemRevision,
                        null,
                        null)
                    : null))
            .ToArray();
        return new CorpseViewSnapshotResponse(
            corpseId,
            null,
            "Fallen Hero",
            "local-city-1",
            0,
            0,
            -1,
            "player_corpse",
            revision,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            [
                new CorpseViewSectionResponse(
                    sectionKind,
                    containerId,
                    "corpse_" + sectionKind,
                    containerRevision,
                    slots.Length,
                    slots)
            ],
            Array.Empty<CorpsePresentationSnapshotResponse>());
    }
}
