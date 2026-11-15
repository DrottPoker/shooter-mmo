using System.Net;
using Microsoft.Extensions.Logging;

namespace Mmorpg.SimulationWorker.Realtime;

// One guarded unit of the realtime loop: a tick phase or a step of a loop iteration. It counts
// its failed runs in a row; a run that succeeds resets the count. Loop-confined.
internal sealed class RealtimeLoopStep
{
    internal RealtimeLoopStep(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Kind = kind;
    }

    public string Name { get; }

    // "tick phase" or "loop step", for the log lines.
    public string Kind { get; }

    public int ConsecutiveFailures { get; private set; }

    // Called after every run that did not throw.
    public void Succeeded()
    {
        ConsecutiveFailures = 0;
    }

    internal int RecordFailure()
    {
        return ++ConsecutiveFailures;
    }
}

// Keeps one unexpected exception in the realtime loop from stopping the Shard. A tick phase or
// loop step that throws is logged, counted, and skipped for this run; one that fails
// FailureLimit runs in a row ends the loop, so the ordered shutdown runs instead of keeping
// players in a world that no longer moves. A failure that concerns one player or one peer only
// takes that one out, through RealtimeFailureIsolation, and is counted here too. The loop thread
// writes the counts; TotalFailures may be read from any thread.
internal sealed class RealtimeLoopGuard
{
    public const string TickPhaseKind = "tick phase";
    public const string LoopStepKind = "loop step";

    private readonly RealtimeSimulationClock clock;
    private readonly RealtimePerformanceMetrics metrics;
    private readonly ILogger logger;
    private long totalFailures;

    public RealtimeLoopGuard(
        int failureLimit,
        RealtimeSimulationClock clock,
        RealtimePerformanceMetrics metrics,
        ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureLimit, 1);
        FailureLimit = failureLimit;
        this.clock = clock;
        this.metrics = metrics;
        this.logger = logger;
    }

    public int FailureLimit { get; }

    // Every failure since the start: failed steps and isolated players and peers.
    public long TotalFailures => Interlocked.Read(ref totalFailures);

    public RealtimeLoopStep TickPhase(string name)
    {
        return new RealtimeLoopStep(name, TickPhaseKind);
    }

    public RealtimeLoopStep LoopStep(string name)
    {
        return new RealtimeLoopStep(name, LoopStepKind);
    }

    // Called from the catch block of a step's run. Throws once the step failed FailureLimit
    // runs in a row, which ends the loop the way an unhandled exception does.
    public void Failed(RealtimeLoopStep step, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(step);
        var consecutiveFailures = step.RecordFailure();
        Interlocked.Increment(ref totalFailures);
        metrics.RecordLoopFailure(step.Name, RealtimeLoopFailureScope.Step);
        logger.LogError(
            exception,
            "[SIMULATION] The {StepKind} {StepName} failed at server tick {ServerTick}, {ConsecutiveFailures} of at most {FailureLimit} runs in a row; the loop goes on without it.",
            step.Kind,
            step.Name,
            clock.ServerTick,
            consecutiveFailures,
            FailureLimit);
        if (consecutiveFailures < FailureLimit)
        {
            return;
        }

        logger.LogCritical(
            exception,
            "[SIMULATION] The {StepKind} {StepName} failed {ConsecutiveFailures} runs in a row at server tick {ServerTick}. Stopping the worker.",
            step.Kind,
            step.Name,
            consecutiveFailures,
            clock.ServerTick);
        throw new InvalidOperationException(
            $"The {step.Kind} '{step.Name}' failed {consecutiveFailures} runs in a row.",
            exception);
    }

    // One player's own state failed in the step; only that player leaves the Shard.
    public void PlayerFailed(string stepName, ulong entityId, Guid characterId, Exception exception)
    {
        Interlocked.Increment(ref totalFailures);
        metrics.RecordLoopFailure(stepName, RealtimeLoopFailureScope.Player);
        logger.LogError(
            exception,
            "[SIMULATION] The {StepName} of player entity {EntityId} of character {CharacterId} failed at server tick {ServerTick}; only this player leaves the Shard.",
            stepName,
            entityId,
            characterId,
            clock.ServerTick);
    }

    // The step failed for one peer only; the caller disconnects it or it is gone already.
    public void PeerFailed(string stepName, int peerId, Exception exception)
    {
        Interlocked.Increment(ref totalFailures);
        metrics.RecordLoopFailure(stepName, RealtimeLoopFailureScope.Peer);
        logger.LogError(
            exception,
            "[SIMULATION] The {StepName} failed for UDP peer {PeerId} at server tick {ServerTick}; the loop goes on without this peer.",
            stepName,
            peerId,
            clock.ServerTick);
    }

    // Deciding a connection request failed; only that request is refused.
    public void ConnectionRequestFailed(EndPoint endPoint, Exception exception)
    {
        Interlocked.Increment(ref totalFailures);
        metrics.RecordLoopFailure("connection request", RealtimeLoopFailureScope.Peer);
        logger.LogError(
            exception,
            "[SIMULATION] Could not decide the connection request from {EndPoint} at server tick {ServerTick}; only this request is refused.",
            endPoint,
            clock.ServerTick);
    }
}

// What a counted loop failure took out: the run of a whole step, one player, or one peer.
internal enum RealtimeLoopFailureScope
{
    Step,
    Player,
    Peer
}
