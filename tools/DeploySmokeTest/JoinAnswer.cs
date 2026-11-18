using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.DeploySmokeTest;

// The answer of POST /api/shards/{shardId}/join and the one place that turns it into what the bot
// client library needs. A change of the join ticket or of the realtime handshake changes this file
// only: the rest of the smoke test speaks the library's public surface.
public sealed record JoinAnswer(
    ShardAnswer Shard,
    EndpointAnswer Endpoint,
    Guid CharacterId,
    string JoinTicket,
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
            JoinTicket,
            Shard.Id,
            Shard.WorldId);
    }
}
