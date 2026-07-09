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
    public sealed class WorldResponse
    {
        public string id;
        public string displayName;
        public string host;
        public int udpPort;
        public string ruleSet;
        public bool isOnline;
    }

    [Serializable]
    public sealed class JoinWorldRequest
    {
        public string characterId;
    }

    [Serializable]
    public sealed class JoinWorldResponse
    {
        public WorldResponse world;
        public string characterId;
        public string joinTicket;
        public string expiresAt;
    }

    [Serializable]
    public sealed class DebugJoinRequest
    {
        public string joinTicket;
    }

    [Serializable]
    public sealed class ActivePlayerSessionResponse
    {
        public string accountId;
        public string characterId;
        public string characterName;
        public string worldId;
        public string joinedAt;
    }
}

