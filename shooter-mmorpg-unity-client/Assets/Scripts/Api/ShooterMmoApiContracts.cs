using System;

namespace ShooterMmo.Api
{
    [Serializable]
    public sealed class RegisterAccountRequest
    {
        public string email;
        public string username;
        public string password;
    }

    [Serializable]
    public sealed class LoginAccountRequest
    {
        public string login;
        public string password;
    }

    [Serializable]
    public sealed class AuthResponse
    {
        public string accountId;
        public string username;
        public string sessionId;
        public string sessionToken;
        public string expiresAt;
    }

    [Serializable]
    public sealed class AccountProfileResponse
    {
        public string accountId;
        public string email;
        public string username;
        public string createdAt;
    }

    [Serializable]
    public sealed class CreateCharacterRequest
    {
        public string name;
    }

    [Serializable]
    public sealed class CharacterResponse
    {
        public string id;
        public string name;
        public long currency;
        public string createdAt;
    }

    [Serializable]
    public sealed class ShardResponse
    {
        public string id;
        public string displayName;
        public string worldId;
        public string fleetId;
        public string fleetDisplayName;
        public string regionCode;
        public string ruleSet;
        public bool isOnline;
        public int activePlayers;
        public int capacity;
    }

    [Serializable]
    public sealed class SimulationEndpointResponse
    {
        public string workerId;
        public string runtimeId;
        public string host;
        public int udpPort;
        public int protocolVersion;
        public string simulationRevision;
        public string collisionRevision;
    }

    [Serializable]
    public sealed class JoinShardRequest
    {
        public string characterId;
    }

    [Serializable]
    public sealed class JoinShardResponse
    {
        public ShardResponse shard;
        public SimulationEndpointResponse endpoint;
        public string characterId;
        public string joinTicket;
        public string expiresAt;
        public bool isReconnect;
    }

    [Serializable]
    public sealed class ActiveSimulationSessionResponse
    {
        public string simulationSessionId;
        public string accountId;
        public string characterId;
        public string characterName;
        public string shardId;
        public string worldId;
        public string workerId;
        public string workerRuntimeId;
        public string joinedAt;
        public string sessionExpiresAt;
        public bool isReconnect;
    }
}
