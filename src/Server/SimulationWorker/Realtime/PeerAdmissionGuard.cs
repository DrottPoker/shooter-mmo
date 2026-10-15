using System.Diagnostics;
using System.Net;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

// Limits remote peers that have not joined yet, so one host cannot fill every slot or
// reset its per-peer quotas by reconnecting. Loopback peers are local development bots
// and stress tools and are exempt. Used only from the simulation loop thread.
public sealed class PeerAdmissionGuard(AdmissionConfig config)
{
    private static readonly TimeSpan IdleAddressRetention = TimeSpan.FromMinutes(5);

    private readonly Dictionary<int, IPAddress> unauthenticatedPeers = [];
    private readonly Dictionary<IPAddress, AddressState> addresses = [];
    private long nextCleanupTimestamp;

    public int UnauthenticatedRemotePeerCount => unauthenticatedPeers.Count;

    public string? TryAdmit(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return null;
        }

        RemoveIdleAddresses();
        var state = GetState(address);
        state.LastSeenTimestamp = Stopwatch.GetTimestamp();
        if (!state.ConnectionAttempts.TryConsume(1d))
        {
            return "connection_rate_limited";
        }

        if (unauthenticatedPeers.Count >= config.MaxUnauthenticatedPeers)
        {
            return "too_many_pending_peers";
        }

        return state.UnauthenticatedPeers >= config.MaxUnauthenticatedPeersPerAddress
            ? "too_many_pending_peers_for_address"
            : null;
    }

    public void OnPeerConnected(int peerId, IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || !unauthenticatedPeers.TryAdd(peerId, address))
        {
            return;
        }

        GetState(address).UnauthenticatedPeers++;
    }

    public void OnPeerAuthenticated(int peerId)
    {
        Release(peerId);
    }

    public void OnPeerDisconnected(int peerId)
    {
        Release(peerId);
    }

    private void Release(int peerId)
    {
        if (unauthenticatedPeers.Remove(peerId, out var address)
            && addresses.TryGetValue(address, out var state))
        {
            state.UnauthenticatedPeers = Math.Max(0, state.UnauthenticatedPeers - 1);
            state.LastSeenTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private AddressState GetState(IPAddress address)
    {
        if (!addresses.TryGetValue(address, out var state))
        {
            state = new AddressState(new TokenBucket(
                config.ConnectionAttemptsPerAddressPerSecond,
                config.ConnectionAttemptBurstPerAddress));
            addresses.Add(address, state);
        }

        return state;
    }

    private void RemoveIdleAddresses()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < nextCleanupTimestamp)
        {
            return;
        }

        nextCleanupTimestamp = now + Stopwatch.Frequency * 60;
        foreach (var (address, state) in addresses.ToArray())
        {
            if (state.UnauthenticatedPeers == 0
                && Stopwatch.GetElapsedTime(state.LastSeenTimestamp, now) > IdleAddressRetention)
            {
                addresses.Remove(address);
            }
        }
    }

    private sealed class AddressState(TokenBucket connectionAttempts)
    {
        public TokenBucket ConnectionAttempts { get; } = connectionAttempts;

        public int UnauthenticatedPeers { get; set; }

        public long LastSeenTimestamp { get; set; } = Stopwatch.GetTimestamp();
    }
}
