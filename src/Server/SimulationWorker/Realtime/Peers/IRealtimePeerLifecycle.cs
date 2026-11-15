using System.Net;
using LiteNetLib;

namespace Mmorpg.SimulationWorker.Realtime;

// What the transport tells when a peer connected or disconnected; RealtimePeerLifecycle in the
// worker, a recording or failing fake in the transport tests. Loop-confined.
internal interface IRealtimePeerLifecycle
{
    void OnPeerConnected(int peerId, IPAddress address);

    void OnPeerDisconnected(int peerId, DisconnectReason reason);
}
