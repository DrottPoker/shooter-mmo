using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.SimulationBotClient;

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

// The bot's newest authoritative position, from the join or the latest owner snapshot.
public readonly record struct SimulationBotPosition(float X, float Y, float Z);

public interface ISimulationBotInputSource
{
    // The path to follow this tick, or null to stand. Returning the same path keeps
    // following it, and a new route needs a new path id.
    RealtimeMovementPath? SelectPath(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position);
}

// Sends one encoded message on the channel and with the delivery its protocol descriptor names.
public interface ISimulationBotTransport
{
    void Send(byte[] packet, byte channel, RealtimeDelivery delivery);
}

public interface ISimulationBotRealtimeObserver
{
    void OnJoined(RealtimeJoinAccepted session);

    void OnItemOperationResult(RealtimeItemOperationResult result);

    void OnCorpsePresence(RealtimeCorpsePresenceSnapshotChunk chunk);

    void OnCorpseInteractionResult(RealtimeCorpseInteractionResult result);

    void OnCorpseViewState(RealtimeCorpseViewStateChunk chunk);

    void OnCorpseViewClosed(RealtimeCorpseViewClosed closed);
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
