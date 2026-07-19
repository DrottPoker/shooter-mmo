using ShooterMmo.GameProtocol;
using ShooterMmo.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = ShooterMmo.Tools.SimulationBotClient.SimulationBotClient;

namespace ShooterMmo.Tools.StackStressGenerator;

public enum StressBotState
{
    Created,
    Connecting,
    Joining,
    Joined,
    Leaving,
    Completed,
    Failed
}

public sealed class StressBot : IDisposable
{
    private readonly HeadlessSimulationBotClient client;

    public StressBot(
        StressGeneratorOptions options,
        StressBotAdmission admission,
        StressLatencyAccumulator inputAcknowledgementLatencies,
        StressLatencyAccumulator intervalInputAcknowledgementLatencies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(inputAcknowledgementLatencies);
        ArgumentNullException.ThrowIfNull(intervalInputAcknowledgementLatencies);

        client = new HeadlessSimulationBotClient(
            new SimulationBotConnectionOptions(
                admission.WorkerHost,
                admission.WorkerUdpPort,
                options.JoinTimeout),
            new SimulationBotIdentity(
                admission.BotIndex,
                admission.AccountId,
                admission.CharacterId,
                admission.CharacterName,
                admission.Ticket,
                admission.ShardId,
                admission.WorldId),
            new StressBotInputSource(admission.BotIndex, options.Seed),
            latencyMilliseconds =>
            {
                inputAcknowledgementLatencies.Record(latencyMilliseconds);
                intervalInputAcknowledgementLatencies.Record(latencyMilliseconds);
            });
    }

    public StressBotState State => (StressBotState)client.State;

    public void Start()
    {
        client.Start();
    }

    public void Poll(long nowTimestamp)
    {
        client.Poll(nowTimestamp);
    }

    public void BeginLeave(long nowTimestamp)
    {
        client.BeginLeave(nowTimestamp);
    }

    public StressBotSnapshot Capture()
    {
        var snapshot = client.Capture();
        return new StressBotSnapshot(
            snapshot.BotIndex,
            snapshot.CharacterName,
            (StressBotState)snapshot.State,
            snapshot.JoinLatencyMs,
            snapshot.PacketsSent,
            snapshot.BytesSent,
            snapshot.PacketsReceived,
            snapshot.BytesReceived,
            snapshot.SnapshotsReceived,
            snapshot.EstimatedMissingSnapshots,
            snapshot.SpawnPackets,
            snapshot.DespawnPackets,
            snapshot.UnacknowledgedInputsDropped,
            snapshot.LatestServerTick,
            snapshot.FailureCode,
            snapshot.FailureMessage);
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private sealed class StressBotInputSource(int botIndex, int seed) :
        ISimulationBotInputSource
    {
        public RealtimeMovementInput CreateInput(
            uint inputSequence,
            uint clientTick,
            RealtimeJoinAccepted joinedSession)
        {
            var ticksPerPhase = Math.Max(1u, joinedSession.MovementSettings.TickRateHz * 2u);
            var phase = (int)(((clientTick / ticksPerPhase) + (uint)(botIndex + seed)) % 4u);
            var (moveX, moveY) = phase switch
            {
                0 => (0f, 1f),
                1 => (1f, 0f),
                2 => (0f, -1f),
                _ => (-1f, 0f)
            };
            var buttons = phase == 3
                ? RealtimeMovementButtons.Aim
                : RealtimeMovementButtons.Sprint;
            var jumpInterval = Math.Max(1u, joinedSession.MovementSettings.TickRateHz * 5u);
            if (phase != 3 && clientTick % jumpInterval == (uint)(botIndex % jumpInterval))
            {
                buttons |= RealtimeMovementButtons.Jump;
            }

            var yaw = (botIndex * 17f + clientTick * 1.5f) % 360f;
            return new RealtimeMovementInput(
                inputSequence,
                clientTick,
                moveX,
                moveY,
                yaw,
                buttons);
        }
    }
}

public sealed record StressBotSnapshot(
    int BotIndex,
    string CharacterName,
    StressBotState State,
    double? JoinLatencyMs,
    long PacketsSent,
    long BytesSent,
    long PacketsReceived,
    long BytesReceived,
    long SnapshotsReceived,
    long EstimatedMissingSnapshots,
    long SpawnPackets,
    long DespawnPackets,
    long UnacknowledgedInputsDropped,
    uint LatestServerTick,
    string? FailureCode,
    string? FailureMessage);
