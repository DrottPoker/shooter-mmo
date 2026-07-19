using SimulationWorker.Auth;
using SimulationWorker.Corpses;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class DurableCorpseStoreTests
{
    [Fact]
    public void ReplaceUsesDatabaseClockAndKeepsEmptyCorpsesUntilAbsoluteExpiry()
    {
        var databaseTime = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(
            2030,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero));
        var store = new DurableCorpseStore(timeProvider);
        var corpse = CreateCorpse(
            Guid.NewGuid(),
            databaseTime.AddMinutes(-1),
            databaseTime.AddMinutes(4),
            isEmpty: true);

        store.Replace(new CorpseRestoreResponse(databaseTime, [corpse]), "local-shard-1");

        Assert.Equal(corpse.CorpseId, Assert.Single(store.ListActive()).CorpseId);
        timeProvider.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(59)));
        Assert.Single(store.ListActive());
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.ListActive());
    }

    [Fact]
    public void ReplaceRejectsDuplicateOrCrossShardStateAndFiltersAlreadyExpiredRows()
    {
        var databaseTime = DateTime.UtcNow;
        var store = new DurableCorpseStore(TimeProvider.System);
        var corpseId = Guid.NewGuid();
        var active = CreateCorpse(
            corpseId,
            databaseTime.AddMinutes(-1),
            databaseTime.AddMinutes(4),
            isEmpty: false);
        var expired = CreateCorpse(
            Guid.NewGuid(),
            databaseTime.AddMinutes(-5),
            databaseTime,
            isEmpty: true);

        store.Replace(new CorpseRestoreResponse(databaseTime, [active, expired]), "local-shard-1");
        Assert.Equal(corpseId, Assert.Single(store.ListActive()).CorpseId);

        Assert.Throws<ArgumentException>(() => store.Replace(
            new CorpseRestoreResponse(databaseTime, [active, active]),
            "local-shard-1"));
        Assert.Throws<ArgumentException>(() => store.Replace(
            new CorpseRestoreResponse(
                databaseTime,
                [active with { ShardId = "another-shard" }]),
            "local-shard-1"));
    }

    [Fact]
    public void ApplySnapshotDoesNotRegressAnExistingCorpseRevision()
    {
        var databaseTime = DateTime.UtcNow;
        var corpseId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var store = new DurableCorpseStore(TimeProvider.System);
        store.Replace(
            new CorpseRestoreResponse(
                databaseTime,
                [CreateCorpse(
                    corpseId,
                    databaseTime.AddMinutes(-1),
                    databaseTime.AddMinutes(4),
                    isEmpty: false) with
                {
                    Revision = 5,
                    Sections =
                    [
                        new CorpseSectionResponse(
                            "general_inventory",
                            containerId,
                            5,
                            1),
                        new CorpseSectionResponse(
                            "equipment",
                            Guid.NewGuid(),
                            5,
                            0),
                        new CorpseSectionResponse("bag", Guid.NewGuid(), 5, 0)
                    ]
                }]),
            "local-shard-1");

        store.ApplySnapshot(CreateSnapshot(
            corpseId,
            containerId,
            revision: 4,
            databaseTime.AddMinutes(-1),
            databaseTime.AddMinutes(4)));

        var active = Assert.Single(store.ListActive());
        Assert.Equal(5, active.Revision);
        Assert.Equal(5, Assert.Single(
            active.Sections,
            section => section.SectionKind == "general_inventory").ContainerRevision);
    }

    [Fact]
    public void RepeatedRestoreDoesNotRegressConcurrentLocalState()
    {
        var databaseTime = DateTime.UtcNow;
        var corpseId = Guid.NewGuid();
        var original = CreateCorpse(
            corpseId,
            databaseTime.AddMinutes(-1),
            databaseTime.AddMinutes(4),
            isEmpty: false) with
        { Revision = 5 };
        var store = new DurableCorpseStore(TimeProvider.System);
        store.Replace(
            new CorpseRestoreResponse(databaseTime, [original]),
            "local-shard-1");
        store.Apply(original with { Revision = 6, IsEmpty = true });

        store.Replace(
            new CorpseRestoreResponse(databaseTime.AddSeconds(1), [original]),
            "local-shard-1");

        var active = Assert.Single(store.ListActive());
        Assert.Equal(6, active.Revision);
        Assert.True(active.IsEmpty);
    }

    [Fact]
    public void RestoreSnapshotPreservesCorpseCreatedAfterItsDatabaseTime()
    {
        var databaseTime = DateTime.UtcNow;
        var createdAfterSnapshot = CreateCorpse(
            Guid.NewGuid(),
            databaseTime.AddMilliseconds(1),
            databaseTime.AddMinutes(4),
            isEmpty: false);
        var store = new DurableCorpseStore(TimeProvider.System);
        store.Apply(createdAfterSnapshot);

        store.Replace(
            new CorpseRestoreResponse(databaseTime, []),
            "local-shard-1");

        Assert.Equal(createdAfterSnapshot.CorpseId, Assert.Single(store.ListActive()).CorpseId);
    }

    private static DurableCorpseResponse CreateCorpse(
        Guid corpseId,
        DateTime createdAt,
        DateTime expiresAt,
        bool isEmpty)
    {
        var itemCount = isEmpty ? 0 : 1;
        return new DurableCorpseResponse(
            corpseId,
            Guid.NewGuid(),
            "Fallen Hero",
            "local-shard-1",
            1d,
            2d,
            3d,
            0d,
            0d,
            0d,
            1d,
            "corpse.generic_loot_crate",
            1,
            createdAt,
            expiresAt,
            isEmpty,
            [
                new CorpseSectionResponse(
                    "general_inventory",
                    Guid.NewGuid(),
                    1,
                    itemCount),
                new CorpseSectionResponse("equipment", Guid.NewGuid(), 1, 0),
                new CorpseSectionResponse("bag", Guid.NewGuid(), 1, 0)
            ]);
    }

    private static CorpseViewSnapshotResponse CreateSnapshot(
        Guid corpseId,
        Guid generalContainerId,
        long revision,
        DateTime createdAt,
        DateTime expiresAt)
    {
        return new CorpseViewSnapshotResponse(
            corpseId,
            Guid.NewGuid(),
            "Fallen Hero",
            "local-shard-1",
            1d,
            2d,
            3d,
            "corpse.generic_loot_crate",
            revision,
            createdAt,
            expiresAt,
            [
                new CorpseViewSectionResponse(
                    "general_inventory",
                    generalContainerId,
                    "corpse_general_inventory",
                    revision,
                    1,
                    [new CorpseViewSlotResponse(0, "general", [], null)]),
                new CorpseViewSectionResponse(
                    "equipment",
                    Guid.NewGuid(),
                    "corpse_equipment",
                    revision,
                    1,
                    [new CorpseViewSlotResponse(0, "general", [], null)]),
                new CorpseViewSectionResponse(
                    "bag",
                    Guid.NewGuid(),
                    "corpse_bag",
                    revision,
                    1,
                    [new CorpseViewSlotResponse(0, "general", [], null)])
            ],
            []);
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
