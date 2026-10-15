using System.Diagnostics.CodeAnalysis;

namespace Mmorpg.SimulationWorker.Realtime;

// Resolves and disconnects transport peers by id. Only the realtime loop may call it.
internal interface IRealtimePeerDirectory
{
    bool TryGetConnectedPeer(
        int peerId,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer);

    void DisconnectPeer(IRealtimePeerConnection peer);
}
