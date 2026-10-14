using System.Net;
using SimulationWorker.Config;
using SimulationWorker.Realtime;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class PeerAdmissionGuardTests
{
    private static readonly IPAddress RemoteAddress = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress OtherRemoteAddress = IPAddress.Parse("203.0.113.20");

    [Fact]
    public void OneRemoteAddressCannotHoldMoreThanItsPendingPeerLimit()
    {
        var guard = new PeerAdmissionGuard(new AdmissionConfig(256, 2, 100, 100, 1024));

        Assert.Null(guard.TryAdmit(RemoteAddress));
        guard.OnPeerConnected(1, RemoteAddress);
        Assert.Null(guard.TryAdmit(RemoteAddress));
        guard.OnPeerConnected(2, RemoteAddress);

        Assert.Equal("too_many_pending_peers_for_address", guard.TryAdmit(RemoteAddress));
        Assert.Null(guard.TryAdmit(OtherRemoteAddress));

        // A peer that joins no longer counts as pending.
        guard.OnPeerAuthenticated(1);
        Assert.Null(guard.TryAdmit(RemoteAddress));
    }

    [Fact]
    public void PendingRemotePeersHaveAGlobalLimit()
    {
        var guard = new PeerAdmissionGuard(new AdmissionConfig(1, 8, 100, 100, 1024));
        guard.OnPeerConnected(1, RemoteAddress);

        Assert.Equal("too_many_pending_peers", guard.TryAdmit(OtherRemoteAddress));

        guard.OnPeerDisconnected(1);
        Assert.Null(guard.TryAdmit(OtherRemoteAddress));
    }

    [Fact]
    public void ReconnectingQuicklyIsRateLimitedPerAddress()
    {
        var guard = new PeerAdmissionGuard(new AdmissionConfig(256, 8, 1, 2, 1024));

        Assert.Null(guard.TryAdmit(RemoteAddress));
        Assert.Null(guard.TryAdmit(RemoteAddress));
        Assert.Equal("connection_rate_limited", guard.TryAdmit(RemoteAddress));
    }

    [Fact]
    public void LoopbackDevelopmentClientsAreExempt()
    {
        var guard = new PeerAdmissionGuard(new AdmissionConfig(1, 1, 1, 1, 1024));
        for (var peerId = 0; peerId < 10; peerId++)
        {
            Assert.Null(guard.TryAdmit(IPAddress.Loopback));
            guard.OnPeerConnected(peerId, IPAddress.Loopback);
        }

        Assert.Equal(0, guard.UnauthenticatedRemotePeerCount);
    }
}
