using System.Net;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.SimulationWorker.Realtime;

// Decides whether a UDP connection request may complete. Without a valid registration
// lease or with every slot taken the request is refused silently; an admission guard
// refusal is counted as a rejected connection. Loop-confined, like the guard.
internal sealed class RealtimeConnectionAdmission(
    SimulationWorkerConfig config,
    SimulationWorkerRegistrationLease? registrationLease,
    PeerAdmissionGuard admissionGuard,
    RealtimeNetworkMetrics networkMetrics)
{
    public bool TryAdmit(IPAddress address, int connectedPeersCount)
    {
        if ((registrationLease is not null && !registrationLease.IsValid)
            || connectedPeersCount >= config.MaxConnections)
        {
            return false;
        }

        var admissionRejection = admissionGuard.TryAdmit(address);
        if (admissionRejection is not null)
        {
            networkMetrics.RecordConnectionRejected();
            return false;
        }

        return true;
    }
}
