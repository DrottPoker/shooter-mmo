using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mmorpg.SimulationWorker.Sessions;

// What presence costs and where it goes wrong, as the worker sees it: the characters whose bodies
// stand in the world without a peer, how bodies end, whether reconnects resume a body or place a
// new entity, what became of the saved places and vitals, and the session leases and releases that
// keep a character's session alive. Labels are fixed low-cardinality reasons, never an identity.
// Every recording happens where the event already happens, so the realtime loop adds no work of
// its own per tick.
public sealed class PresenceMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.Presence";

    // How a body ended.
    public const string BodyTimeUp = "time_up";
    public const string BodyDied = "died";
    public const string BodySessionEnded = "session_ended";
    public const string BodyResumed = "resumed";
    public const string BodyWorkerStopped = "worker_stopped";

    // What a reconnect's join did with the character's entity.
    public const string ReconnectResumed = "resumed";
    public const string ReconnectNewEntity = "new_entity";

    // What became of the states that a checkpoint sent.
    public const string CheckpointSent = "sent";
    public const string CheckpointSaved = "saved";
    public const string CheckpointSkipped = "skipped";
    public const string CheckpointFailed = "failed";

    // How a session ended on the worker without GameService's release.
    public const string SessionLeaseExpired = "lease_expired";
    public const string SessionInvalidated = "invalidated";

    // How a release of a session ended.
    public const string ReleaseReleased = "released";
    public const string ReleaseFailed = "failed";
    public const string ReleaseGaveUp = "gave_up";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> bodiesEnded;
    private readonly Counter<long> reconnects;
    private readonly Counter<long> checkpointStates;
    private readonly Counter<long> heartbeatRounds;
    private readonly Counter<long> sessionsEnded;
    private readonly Counter<long> releases;
    private readonly Counter<long> joinsRefused;
    private long oldestUnsavedTimestamp;
    private volatile Func<int>? lingeringBodies;

    public PresenceMetrics()
    {
        meter.CreateObservableGauge(
            "simulation_worker.presence.lingering_bodies",
            () => lingeringBodies?.Invoke() ?? 0);
        meter.CreateObservableGauge(
            "simulation_worker.presence.checkpoint_oldest_unsaved_age",
            ObserveOldestUnsavedAge,
            "s");
        bodiesEnded = meter.CreateCounter<long>("simulation_worker.presence.bodies_ended");
        reconnects = meter.CreateCounter<long>("simulation_worker.presence.reconnects");
        checkpointStates = meter.CreateCounter<long>("simulation_worker.presence.checkpoint_states");
        heartbeatRounds = meter.CreateCounter<long>("simulation_worker.presence.session_heartbeat_rounds");
        sessionsEnded = meter.CreateCounter<long>("simulation_worker.presence.sessions_ended_locally");
        releases = meter.CreateCounter<long>("simulation_worker.presence.releases");
        joinsRefused = meter.CreateCounter<long>("simulation_worker.presence.joins_refused");
    }

    // The lingering bodies that the gauge counts, read when the metrics are exported.
    public void ObserveBodies(Func<int> count)
    {
        lingeringBodies = count;
    }

    public void RecordBodyEnded(string reason, int count = 1)
    {
        if (count > 0)
        {
            bodiesEnded.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    // The join of a reconnect resumed the character's entity, or placed a new one from the state
    // that its session kept.
    public void RecordReconnect(string result)
    {
        reconnects.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    public void RecordCheckpointStates(string outcome, int count)
    {
        if (count > 0)
        {
            checkpointStates.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    // The monotonic timestamp from which the oldest state that GameService has not confirmed has
    // been waiting, or null when every state that the latest pass saw is confirmed.
    public void ObserveOldestUnsaved(long? timestamp)
    {
        Volatile.Write(ref oldestUnsavedTimestamp, timestamp ?? 0);
    }

    // A heartbeat round that had sessions to renew, and whether every request of it went through.
    public void RecordHeartbeatRound(bool renewed)
    {
        heartbeatRounds.Add(1, new KeyValuePair<string, object?>("outcome", renewed ? "renewed" : "failed"));
    }

    // A session that the worker ended locally: its lease ran out, or it was invalidated.
    public void RecordSessionEnded(string reason)
    {
        sessionsEnded.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordRelease(string outcome)
    {
        releases.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    // A join that lost the session to a join that consumed its ticket later.
    public void RecordJoinRefused(string reason)
    {
        joinsRefused.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<double>> ObserveOldestUnsavedAge()
    {
        var timestamp = Volatile.Read(ref oldestUnsavedTimestamp);
        return timestamp == 0
            ? []
            : [new Measurement<double>(Stopwatch.GetElapsedTime(timestamp).TotalSeconds)];
    }
}
