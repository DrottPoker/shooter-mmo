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

    [Serializable]
    public sealed class CharacterInventorySnapshotResponse
    {
        public string characterId;
        public string catalogRevision;
        public long itemStateRevision;
        public ItemContainerSnapshotResponse permanentInventory;
        public EquipmentSlotSnapshotResponse[] equipment;
        public EquippedBagSnapshotResponse equippedBag;
        public ItemContainerSnapshotResponse bank;
        public SecureContainerSnapshotResponse secureContainer;
        public RecoveryStorageSnapshotResponse recoveryStorage;
        public long carriedWeight;
        public long carryCapacity;
        public int loadRatioBasisPoints;
        public bool sprintEligible;
        public int movementMultiplierBasisPoints;
    }

    [Serializable]
    public sealed class ItemContainerSnapshotResponse
    {
        public string containerId;
        public string containerType;
        public long revision;
        public int slotCapacity;
        public ItemSlotSnapshotResponse[] slots;
    }

    [Serializable]
    public sealed class ItemSlotSnapshotResponse
    {
        public int slotIndex;
        public string slotKind;
        public string[] acceptedTags;
        public ItemInstanceSnapshotResponse item;
    }

    [Serializable]
    public sealed class EquipmentSlotSnapshotResponse
    {
        public string equipmentSlotId;
        public int sortOrder;
        public ItemInstanceSnapshotResponse item;
    }

    [Serializable]
    public sealed class ItemInstanceSnapshotResponse
    {
        public string itemInstanceId;
        public string definitionId;
        public int quantity;
        public long revision;
        public ItemPolicySummaryResponse[] policies;
    }

    [Serializable]
    public sealed class ItemPolicySummaryResponse
    {
        public string policyKind;
        public string status;
    }

    [Serializable]
    public sealed class EquippedBagSnapshotResponse
    {
        public ItemInstanceSnapshotResponse item;
        public ItemContainerSnapshotResponse contents;
    }

    [Serializable]
    public sealed class SecureContainerSnapshotResponse
    {
        public string tierId;
        public long entitlementRevision;
        public ItemContainerSnapshotResponse contents;
    }

    [Serializable]
    public sealed class CharacterBankSnapshotResponse
    {
        public string characterId;
        public string catalogRevision;
        public long itemStateRevision;
        public ItemContainerSnapshotResponse bank;
    }

    [Serializable]
    public sealed class CharacterRecoverySnapshotResponse
    {
        public string characterId;
        public string catalogRevision;
        public long itemStateRevision;
        public RecoveryStorageSnapshotResponse recoveryStorage;
    }

    [Serializable]
    public sealed class RecoveryStorageSnapshotResponse
    {
        public string containerId;
        public long revision;
        public RecoveryDeliverySnapshotResponse[] deliveries;
    }

    [Serializable]
    public sealed class RecoveryDeliverySnapshotResponse
    {
        public string deliveryId;
        public long revision;
        public string sourceKind;
        public string createdAt;
        public string availableAt;
        public string expiresAt;
        public string claimedAt;
        public RecoveryDeliveryItemSnapshotResponse[] items;
    }

    [Serializable]
    public sealed class RecoveryDeliveryItemSnapshotResponse
    {
        public int itemOrder;
        public int containerSlotIndex;
        public ItemInstanceSnapshotResponse item;
    }
}
