using System.Diagnostics.Metrics;

namespace Mmorpg.GameService.Placement;

internal readonly record struct PlacementShardState(
    string ShardId,
    bool IsOnline,
    int ActivePlayers,
    int Capacity);

// Shard availability and load as players see them in the shard list, derived from
// worker leases and simulation sessions, and what happened to the places and vitals that workers
// save. Shards are a small configured set, so the shard id is the only label of the shard gauges.
internal sealed class PlacementMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Placement";
    public const string ShardIdTag = "shard.id";

    // Where a character state came from: the periodic checkpoint or the release of a session.
    public const string CheckpointSource = "checkpoint";
    public const string ReleaseSource = "release";

    // What became of a character state.
    public const string SavedOutcome = "saved";

    // The state's session is not an unreleased, unexpired session of the worker runtime that sent
    // it, or has no World or collision revision to save with.
    public const string SessionNotActiveOutcome = "session_not_active";

    // A state of the same session with an equal or higher sequence is stored already.
    public const string NotNewerOutcome = "not_newer";

    // The session was released before, so this release did not end it and saves no state.
    public const string AlreadyReleasedOutcome = "already_released";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> characterStates;
    private PlacementShardState[] shards = [];

    // Sessions that nothing released although their lease ran out on a worker that still answers;
    // -1 while unknown.
    private int expiredUnreleasedSessions = -1;

    public PlacementMetrics()
    {
        meter.CreateObservableGauge(
            "game_service.placement.shard.online",
            () => Observe(shard => shard.IsOnline ? 1 : 0));
        meter.CreateObservableGauge(
            "game_service.placement.shard.players",
            () => Observe(shard => shard.ActivePlayers));
        meter.CreateObservableGauge(
            "game_service.placement.shard.capacity",
            () => Observe(shard => shard.Capacity));
        meter.CreateObservableGauge(
            "game_service.placement.sessions.expired_unreleased",
            ObserveExpiredUnreleasedSessions);
        characterStates = meter.CreateCounter<long>("game_service.placement.character_states");
    }

    public void ObserveShards(IEnumerable<PlacementShardState> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Volatile.Write(ref shards, current.ToArray());
    }

    // The sessions that a worker which still answers failed to release or renew; null drops the
    // value, since unknown is not zero.
    public void ObserveExpiredUnreleased(int? count)
    {
        Volatile.Write(ref expiredUnreleasedSessions, count ?? -1);
    }

    // Counts character states by where they came from and what became of them: saved, or skipped
    // for one of the reasons above.
    public void RecordCharacterStates(string source, string outcome, int count)
    {
        if (count > 0)
        {
            characterStates.Add(
                count,
                new KeyValuePair<string, object?>("source", source),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<int>> ObserveExpiredUnreleasedSessions()
    {
        var count = Volatile.Read(ref expiredUnreleasedSessions);
        return count < 0 ? [] : [new Measurement<int>(count)];
    }

    private IEnumerable<Measurement<int>> Observe(Func<PlacementShardState, int> selector)
    {
        foreach (var shard in Volatile.Read(ref shards))
        {
            yield return new Measurement<int>(
                selector(shard),
                new KeyValuePair<string, object?>(ShardIdTag, shard.ShardId));
        }
    }
}
