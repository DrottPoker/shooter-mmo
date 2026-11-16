using System.Diagnostics.CodeAnalysis;

namespace Mmorpg.SimulationWorker.Realtime;

// Resolves and disconnects transport peers by id. Only the realtime loop may call it.
internal interface IRealtimePeerDirectory
{
    bool TryGetConnectedPeer(
        int peerId,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer);

    // Disconnects the peer; data, at most RealtimeDisconnectData.MaximumBytes, travels with the
    // disconnect and tells the client why.
    void DisconnectPeer(IRealtimePeerConnection peer, byte[]? data);
}
