using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The client's prediction and the worker on one line that delays, jitters, and loses packets,
// both stepping the shared simulation. In the middle of a walk the line goes down in both
// directions. With the path continuation the worker walks on through the outage, so the inputs
// after it confirm exactly what the client predicted: no correction, and the same place at the
// end. Without it the worker stood and the client snapped back.
public sealed class MovementOutageTests
{
    // Three seconds at 30 Hz, the default PathContinuationSeconds.
    private const int ContinuationSteps = 90;
    private const int OutageStartTick = 60;
    private const int Ticks = 420;

    private static readonly MovementSimulationSettings Settings = new(
        30,
        5f,
        720f,
        -24f,
        -2f,
        0f,
        -14f,
        14f,
        -14f,
        14f);

    // 41 m, about 8 s of walking, which ends well before the run does.
    private static readonly MovementPath Walk = new(
        1,
        [new SimulationVector3(0f, 0f, 12f), new SimulationVector3(12f, 0f, 12f), new SimulationVector3(12f, 0f, -4f)]);

    private static readonly MovementPath Turn = new(2, [new SimulationVector3(-10f, 0f, 6f)]);

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public void AWalkThroughAnOutageNeedsNoCorrectionAndEndsWhereTheClientPredictedIt(int outageTicks)
    {
        var run = Run(outageTicks, ContinuationSteps, turnDuringOutage: false);

        Assert.Equal(0f, run.LargestCorrection);
        AssertSamePlace(run, Walk);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    public void WithoutThePathContinuationTheClientSnapsBackAfterTheOutage(int outageTicks)
    {
        var run = Run(outageTicks, 0, turnDuringOutage: false);

        // The worker stood after its three steps ahead while the client walked on.
        Assert.True(run.LargestCorrection > 3f, $"The largest correction was {run.LargestCorrection} m.");
        AssertSamePlace(run, Walk);
    }

    // The click during the outage reaches the worker only with the newest inputs after it, so the
    // other path starts from where the worker is and the client corrects by the difference, at
    // most the walk of the outage. Both end at the end of the other path.
    [Fact]
    public void AnotherPathClickedDuringTheOutageStartsWhereTheWorkerIsAndBothEndAtItsEnd()
    {
        const int outageTicks = 60;
        var run = Run(outageTicks, ContinuationSteps, turnDuringOutage: true);

        Assert.InRange(run.LargestCorrection, 0.1f, Settings.WalkSpeed * outageTicks / Settings.TickRateHz);
        AssertSamePlace(run, Turn);
    }

    private static RunResult Run(int outageTicks, int continuationSteps, bool turnDuringOutage)
    {
        var collisionWorld = CollisionTestWorldFactory.Create();
        var start = PlayerMovementSimulation.CreateInitialState(Settings, collisionWorld, 0f, 0f, -1f, 0f);
        var worker = new AuthoritativePlayerMovement(
            start,
            Settings,
            PlayerCarryState.Default,
            collisionWorld,
            continuationSteps);
        var client = new PredictingClient(start, collisionWorld);
        var outage = (Start: OutageStartTick, End: OutageStartTick + outageTicks);
        var inputs = new Line<RealtimeMovementInputBatch>(1, outage);
        var snapshots = new Line<(PlayerMovementState State, uint LastProcessed)>(2, outage);
        for (var tick = 0; tick < Ticks; tick++)
        {
            foreach (var batch in inputs.Receive(tick))
            {
                Assert.Equal(MovementInputAcceptance.Accepted, worker.AcceptInputs(batch));
            }

            worker.SimulateTick();
            if (tick % 2 == 0)
            {
                snapshots.Send(tick, (worker.ConfirmedState, worker.LastProcessedInputSequence));
            }

            foreach (var snapshot in snapshots.Receive(tick))
            {
                client.Reconcile(snapshot.State, snapshot.LastProcessed);
            }

            var path = turnDuringOutage && tick >= OutageStartTick + (outageTicks / 2) ? Turn : Walk;
            inputs.Send(tick, client.PredictAndBatch(path));
        }

        return new RunResult(client.LargestCorrection, client.State, worker.State, worker.ConfirmedState);
    }

    private static void AssertSamePlace(RunResult run, MovementPath path)
    {
        var end = path[path.Count - 1];
        Assert.Equal(end.X, run.WorkerState.PositionX, 4);
        Assert.Equal(end.Z, run.WorkerState.PositionZ, 4);
        foreach (var state in new[] { run.ClientState, run.WorkerConfirmedState })
        {
            Assert.Equal(run.WorkerState.PositionX, state.PositionX, 5);
            Assert.Equal(run.WorkerState.PositionY, state.PositionY, 5);
            Assert.Equal(run.WorkerState.PositionZ, state.PositionZ, 5);
        }
    }

    private sealed record RunResult(
        float LargestCorrection,
        PlayerMovementState ClientState,
        PlayerMovementState WorkerState,
        PlayerMovementState WorkerConfirmedState);

    // ClientMovementPrediction on the shared simulation: one step per input, every owner
    // snapshot replays the inputs after its acknowledgement, and each batch carries the newest
    // four inputs and the newest path until a snapshot confirms it.
    private sealed class PredictingClient(PlayerMovementState initialState, ICollisionWorld collisionWorld)
    {
        private const int MaximumPendingInputs = 256;
        private readonly List<PlayerMovementInput> pendingInputs = [];
        private readonly CollisionQueryBuffer collisionQueryBuffer = new();
        private uint confirmedPathId;
        private uint nextInputSequence;

        public PlayerMovementState State { get; private set; } = initialState;

        // The largest distance one reconciliation moved the prediction.
        public float LargestCorrection { get; private set; }

        public RealtimeMovementInputBatch PredictAndBatch(MovementPath path)
        {
            nextInputSequence++;
            var input = new PlayerMovementInput(nextInputSequence, nextInputSequence, path);
            State = Step(State, input);
            pendingInputs.Add(input);
            if (pendingInputs.Count > MaximumPendingInputs)
            {
                pendingInputs.RemoveAt(0);
            }

            var count = Math.Min(RealtimeProtocol.MaximumInputBatchSize, pendingInputs.Count);
            var batch = pendingInputs
                .Skip(pendingInputs.Count - count)
                .Select(pending => new RealtimeMovementInput(pending.InputSequence, pending.ClientTick, pending.PathId))
                .ToArray();
            return new RealtimeMovementInputBatch(batch, path.Id != confirmedPathId ? ToWire(path) : null);
        }

        public void Reconcile(PlayerMovementState authoritativeState, uint lastProcessedInputSequence)
        {
            var previous = State;
            confirmedPathId = authoritativeState.PathProgress.PathId;
            pendingInputs.RemoveAll(input => !MovementSequence.IsNewer(input.InputSequence, lastProcessedInputSequence));
            var replayed = authoritativeState;
            foreach (var input in pendingInputs)
            {
                replayed = Step(replayed, input);
            }

            State = replayed;
            var deltaX = (double)replayed.PositionX - previous.PositionX;
            var deltaZ = (double)replayed.PositionZ - previous.PositionZ;
            LargestCorrection = Math.Max(LargestCorrection, (float)Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ)));
        }

        private PlayerMovementState Step(PlayerMovementState state, PlayerMovementInput input)
        {
            return PlayerMovementSimulation.Step(
                state,
                input,
                Settings,
                PlayerCarryState.Default,
                collisionWorld,
                collisionQueryBuffer);
        }

        private static RealtimeMovementPath ToWire(MovementPath path)
        {
            var waypoints = new RealtimeMovementPathWaypoint[path.Count];
            for (var index = 0; index < path.Count; index++)
            {
                waypoints[index] = new RealtimeMovementPathWaypoint(path[index].X, path[index].Y, path[index].Z);
            }

            return new RealtimeMovementPath(path.Id, waypoints);
        }
    }

    // One direction of the line: 200 ms of delay with up to 67 ms of jitter, 10 % lost at
    // random, and everything sent during the outage lost. Like a sequenced channel it drops a
    // packet that arrives after a newer one.
    private sealed class Line<T>(int seed, (int Start, int End) outage)
    {
        private const int DelayTicks = 6;
        private const int JitterTicks = 2;
        private const int LossPercent = 10;
        private readonly Random random = new(seed);
        private readonly List<(int Due, int Order, T Payload)> inFlight = [];
        private int sent;
        private int newestDelivered = -1;

        public void Send(int tick, T payload)
        {
            var order = sent++;
            var lost = random.Next(100) < LossPercent;
            var due = tick + DelayTicks + random.Next(JitterTicks + 1);
            if (!lost && (tick < outage.Start || tick >= outage.End))
            {
                inFlight.Add((due, order, payload));
            }
        }

        public List<T> Receive(int tick)
        {
            var arrived = inFlight.Where(packet => packet.Due <= tick).OrderBy(packet => packet.Order).ToList();
            inFlight.RemoveAll(packet => packet.Due <= tick);
            var delivered = new List<T>();
            foreach (var packet in arrived)
            {
                if (packet.Order > newestDelivered)
                {
                    newestDelivered = packet.Order;
                    delivered.Add(packet.Payload);
                }
            }

            return delivered;
        }
    }
}
