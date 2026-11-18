using Mmorpg.GameProtocol;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Placement.Development;

namespace Mmorpg.GameService.Testing;

// What a client and a worker make of a join ticket: the join request with the ticket id, a fresh
// nonce, and the proof made with the ticket's transport key, as the Unity client sends it, and
// the consume request that the worker forwards from it.
internal static class JoinTicketProofs
{
    public static RealtimeJoinRequest JoinRequest(JoinShardResponse ticket)
    {
        return JoinRequest(ticket.JoinTicketId, ticket.TransportKey, ticket.Shard.Id);
    }

    // The proof names the Shard that the ticket was issued for.
    public static RealtimeJoinRequest JoinRequest(Guid ticketId, byte[] transportKey, string ticketShardId)
    {
        var proofKey = RealtimeTransportKeys.DeriveProofKey(transportKey);
        try
        {
            return RealtimeJoinProof.CreateRequest(proofKey, ticketId, ticketShardId);
        }
        finally
        {
            Array.Clear(proofKey);
        }
    }

    public static ConsumeSimulationJoinTicketRequest Consume(
        JoinShardResponse ticket,
        string workerId,
        string runtimeId,
        string shardId)
    {
        return Consume(JoinRequest(ticket), workerId, runtimeId, shardId);
    }

    public static ConsumeSimulationJoinTicketRequest Consume(
        DevelopmentSimulationBotTicketResponse ticket,
        string workerId,
        string runtimeId,
        string shardId)
    {
        return Consume(JoinRequest(ticket.JoinTicketId, ticket.TransportKey, ticket.ShardId), workerId, runtimeId, shardId);
    }

    public static ConsumeSimulationJoinTicketRequest Consume(
        RealtimeJoinRequest joinRequest,
        string workerId,
        string runtimeId,
        string shardId)
    {
        return new ConsumeSimulationJoinTicketRequest(
            joinRequest.TicketId,
            joinRequest.ClientNonce,
            joinRequest.Proof,
            RealtimeProtocol.Version,
            workerId,
            runtimeId,
            shardId);
    }
}
