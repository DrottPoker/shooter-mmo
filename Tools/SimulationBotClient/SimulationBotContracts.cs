using ShooterMmo.GameProtocol;

namespace ShooterMmo.Tools.SimulationBotClient;

public enum SimulationBotClientState
{
    Created,
    Connecting,
    Joining,
    Joined,
    Leaving,
    Completed,
    Failed
}

public sealed record SimulationBotIdentity(
    int BotIndex,
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string Ticket,
    string ShardId,
    string WorldId);

public sealed record SimulationBotConnectionOptions(
    string Host,
    int UdpPort,
    TimeSpan JoinAndLeaveTimeout);

public interface ISimulationBotInputSource
{
    RealtimeMovementInput CreateInput(
        uint inputSequence,
        uint clientTick,
        RealtimeJoinAccepted joinedSession);
}

public sealed record SimulationBotClientSnapshot(
    int BotIndex,
    string CharacterName,
    SimulationBotClientState State,
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
