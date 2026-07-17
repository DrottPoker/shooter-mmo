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
