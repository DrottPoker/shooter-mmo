using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.DeploySmokeTest;

// The answer of POST /api/shards/{shardId}/join and the one place that turns it into what the bot
// client library needs. The ticket is a public id and a transport key, which GameService hands out
// once, over TLS, and from which the bot proves itself to the worker and seals its datagrams. The
// key is a secret: nothing prints it. A change of the join ticket or of the realtime handshake
// changes this file only: the rest of the smoke test speaks the library's public surface.
public sealed record JoinAnswer(
    ShardAnswer Shard,
    EndpointAnswer Endpoint,
    Guid CharacterId,
    Guid JoinTicketId,
    byte[] TransportKey,
    DateTime ExpiresAt,
    bool IsReconnect)
{
    public SimulationBotIdentity ToIdentity(int botIndex, Guid accountId, string characterName)
    {
        return new SimulationBotIdentity(
            botIndex,
            accountId,
            CharacterId,
            characterName,
            JoinTicketId,
            TransportKey,
            Shard.Id,
            Shard.WorldId);
    }
}
