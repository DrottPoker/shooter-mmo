using System.Net;
using System.Security.Cryptography;
using System.Text;
using LiteNetLib;
using LiteNetLib.Layers;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The join tickets of the tests. A label names a ticket, and the ticket's public id and transport
// key derive from the label, so a test that names one ticket in two places means the same ticket
// in both, and a client and the scripted GameService agree on its keys.
internal static class RealtimeTestTickets
{
    public static Guid IdOf(string ticket)
    {
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("ticket id:" + ticket))[..16]);
    }

    public static byte[] TransportKeyOf(string ticket)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes("transport key:" + ticket));
    }

    public static RealtimeTransportKeys KeysOf(string ticket)
    {
        return RealtimeTransportKeys.Derive(TransportKeyOf(ticket));
    }

    // The join request of the ticket with a fresh nonce and its proof for the harness's Shard.
    public static RealtimeJoinRequest JoinRequestFor(string ticket, string shardId = RealtimeServerHarness.ShardId)
    {
        using var keys = KeysOf(ticket);
        return RealtimeJoinProof.CreateRequest(keys.ProofKey, IdOf(ticket), shardId);
    }

    public static byte[] EncodeJoin(string ticket)
    {
        return RealtimeProtocol.EncodeJoinRequest(JoinRequestFor(ticket));
    }

    // A LiteNetLib client that seals its datagrams with the ticket's keys, as the Unity client does.
    public static NetManager CreateClient(EventBasedNetListener listener, string ticket)
    {
        var security = new RealtimeClientTransportSecurity();
        using (var keys = KeysOf(ticket))
        {
            security.Install(keys);
        }

        return new NetManager(listener, new RealtimeTestSealingLayer(security))
        {
            ChannelsCount = RealtimeProtocol.ChannelCount,
            MtuOverride = RealtimeDatagram.LiteNetLibMtu
        };
    }

    // Whether the consume request proves the ticket, as GameService checks it.
    public static bool Proves(ConsumeSimulationJoinTicketRequest request, string ticket, string shardId = RealtimeServerHarness.ShardId)
    {
        using var keys = KeysOf(ticket);
        return request.TicketId == IdOf(ticket)
            && RealtimeJoinProof.Verify(
                keys.ProofKey,
                (ushort)request.ProtocolVersion,
                request.TicketId,
                shardId,
                request.ClientNonce,
                request.Proof);
    }
}

// The client side of the sealed transport for the tests' LiteNetLib clients, as the Unity client
// and the bots have it.
internal sealed class RealtimeTestSealingLayer(RealtimeClientTransportSecurity security)
    : PacketLayerBase(RealtimeDatagram.MaximumOverhead)
{
    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        security.ProcessInbound(data, ref length);
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        security.ProcessOutbound(data, ref offset, ref length);
    }
}
