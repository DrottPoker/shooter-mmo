using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class LiveMobCorpseStoreTests
{
    [Fact]
    public void NormalMobCorpseExpiresFromWorkerMemoryAndDoesNotSurviveRestart()
    {
        var start = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(start);
        var firstWorkerStore = new LiveMobCorpseStore(timeProvider);
        var corpse = CreateCorpse(start.UtcDateTime, 120d);

        firstWorkerStore.CreateOrGet(corpse);

        Assert.Equal(corpse.CorpseId, Assert.Single(firstWorkerStore.ListActive()).CorpseId);
        Assert.Empty(new LiveMobCorpseStore(timeProvider).ListActive());
        timeProvider.Advance(TimeSpan.FromSeconds(119));
        Assert.Single(firstWorkerStore.ListActive());
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(firstWorkerStore.ListActive());
    }

    [Fact]
    public void SuccessfulClaimAdvancesRevisionAndRemovesExactlyOneLootEntry()
    {
        var createdAt = DateTime.UtcNow;
        var store = new LiveMobCorpseStore(TimeProvider.System);
        var first = new LiveMobCorpseLootEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "fixture.ore",
            2,
            0);
        var second = new LiveMobCorpseLootEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "fixture.potion",
            1,
            1);
        var corpse = CreateCorpse(createdAt, 120d) with { Loot = [first, second] };
        store.CreateOrGet(corpse);

        var snapshot = store.CompleteClaim(corpse.CorpseId, first.LootEntryId, first.GrantId);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Revision);
        var item = Assert.Single(snapshot.Sections.SelectMany(section => section.Slots),
            slot => slot.Item is not null).Item!;
        Assert.Equal(second.LootEntryId, item.ItemInstanceId);
        Assert.Equal(0, Assert.Single(
            snapshot.Sections.SelectMany(section => section.Slots),
            slot => slot.Item is not null).SlotIndex);
    }

    [Fact]
    public void LootAllClaimsLeaveInOneRevisionAndTheLastClaimEmptiesTheCorpse()
    {
        var store = new LiveMobCorpseStore(TimeProvider.System);
        var first = new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.ore", 2, 0);
        var second = new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.potion", 1, 1);
        var third = new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.arrow", 5, 2);
        var corpse = CreateCorpse(DateTime.UtcNow, 120d) with { Loot = [first, second, third] };
        store.CreateOrGet(corpse);

        var snapshot = store.CompleteClaims(corpse.CorpseId, [first.GrantId, third.GrantId]);
        var emptied = store.CompleteClaims(corpse.CorpseId, [second.GrantId]);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Revision);
        var slot = Assert.Single(snapshot.Sections.SelectMany(section => section.Slots));
        Assert.Equal(second.LootEntryId, slot.Item!.ItemInstanceId);
        Assert.Equal(0, slot.SlotIndex);
        Assert.Null(emptied);
        Assert.True(store.IsEmptied(corpse.CorpseId));
    }

    [Fact]
    public void SnapshotHasEmptyEquipmentAndBagSectionsThatEncode()
    {
        var store = new LiveMobCorpseStore(TimeProvider.System);
        var corpse = CreateCorpse(DateTime.UtcNow, 120d);
        store.CreateOrGet(corpse);

        var snapshot = store.CreateSnapshot(corpse.CorpseId);

        Assert.NotNull(snapshot);
        var general = Assert.Single(snapshot.Sections, section => section.SectionKind == "general_inventory");
        Assert.Equal(1, general.SlotCapacity);
        foreach (var kind in new[] { "equipment", "bag" })
        {
            var section = Assert.Single(snapshot.Sections, candidate => candidate.SectionKind == kind);
            Assert.Equal(0, section.SlotCapacity);
            Assert.Empty(section.Slots);
        }

        var chunks = CorpseRealtimePacketBuilder.BuildSnapshot(snapshot);
        Assert.All(chunks, chunk => Assert.NotEmpty(RealtimeProtocol.EncodeCorpseViewStateChunk(chunk)));
    }

    [Fact]
    public void CompletedLiveCorpseReplayCannotRestoreClaimedLoot()
    {
        var createdAt = DateTime.UtcNow;
        var store = new LiveMobCorpseStore(TimeProvider.System);
        var corpse = CreateCorpse(createdAt, 120d);
        store.CreateOrGet(corpse);
        var loot = Assert.Single(corpse.Loot);

        Assert.Null(store.CompleteClaim(corpse.CorpseId, loot.LootEntryId, loot.GrantId));
        var replay = store.CreateOrGet(corpse);

        Assert.True(replay.IsEmpty);
        Assert.False(store.TryGetActive(corpse.CorpseId, out _));
        Assert.True(store.TryGetForReplay(corpse.CorpseId, out var replayTombstone));
        Assert.True(replayTombstone!.IsEmpty);
        Assert.Empty(store.ListActive());
        Assert.Null(store.CreateSnapshot(corpse.CorpseId));
        Assert.False(store.Remove(corpse.CorpseId));
        Assert.Empty(store.ListActive());
    }

    [Fact]
    public void ClaimingTheLastItemLeavesAnEmptiedCorpseUntilItsDeadline()
    {
        var start = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(start);
        var store = new LiveMobCorpseStore(timeProvider);
        var corpse = CreateCorpse(start.UtcDateTime, 120d);
        store.CreateOrGet(corpse);
        var loot = Assert.Single(corpse.Loot);

        Assert.False(store.IsEmptied(corpse.CorpseId));
        Assert.Null(store.CompleteClaim(corpse.CorpseId, loot.LootEntryId, loot.GrantId));
        Assert.True(store.IsEmptied(corpse.CorpseId));
        Assert.False(store.Remove(corpse.CorpseId));
        Assert.True(store.IsEmptied(corpse.CorpseId));
        timeProvider.Advance(TimeSpan.FromSeconds(120));
        Assert.False(store.IsEmptied(corpse.CorpseId));
        Assert.False(store.IsEmptied(Guid.NewGuid()));
    }

    private static LiveMobCorpseState CreateCorpse(DateTime createdAt, double lifetimeSeconds)
    {
        return new LiveMobCorpseState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "mob.feral_wolf",
            "Feral Wolf",
            "local-shard-1",
            1d,
            0d,
            2d,
            0f,
            MobCorpseLifecycleService.DefaultPresentationKey,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            createdAt,
            createdAt.AddSeconds(lifetimeSeconds),
            [new LiveMobCorpseLootEntry(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "fixture.ore",
                1,
                0)]);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public override long GetTimestamp()
        {
            return timestamp;
        }

        public void Advance(TimeSpan duration)
        {
            utcNow = utcNow.Add(duration);
            timestamp += duration.Ticks;
        }
    }
}
