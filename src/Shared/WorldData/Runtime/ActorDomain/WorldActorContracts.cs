using System;

namespace Mmorpg.WorldData.Actors
{
    public static class WorldActorDataFormat
    {
        public const int Version = 2;
    }

    public static class WorldActorKindIds
    {
        public const string Npc = "npc";
        public const string Mob = "mob";
    }

    public static class WorldActorDispositionIds
    {
        public const string Friendly = "friendly";
        public const string Neutral = "neutral";
        public const string Hostile = "hostile";
    }

    public static class WorldActorDamagePolicyIds
    {
        public const string Invulnerable = "invulnerable";
        public const string Damageable = "damageable";
    }

    public static class WorldActorCorpsePersistenceModeIds
    {
        public const string Live = "live";
        public const string Durable = "durable";
    }

    // An aggressive Mob attacks a player it detects; a defensive Mob fights only once damaged.
    public static class WorldActorAggressionIds
    {
        public const string Aggressive = "aggressive";
        public const string Defensive = "defensive";
    }

    // Bounds for Mob combat, brain, locomotion, and loot profiles.
    public static class WorldActorCombatRules
    {
        public const float MaximumDetectionRange = 64f;
        public const float MaximumLeashRange = 256f;
        public const float MinimumGiveUpSeconds = 1f;
        public const float MaximumGiveUpSeconds = 600f;
        public const float MaximumWalkSpeed = 20f;
        public const float MaximumCollisionRadius = 4f;
        public const float MaximumCollisionHeight = 16f;
        public const int MaximumLootQuantity = 1_000_000;
        public const int MaximumLootChanceBasisPoints = 10_000;
    }

    public static class WorldActorCorpseRules
    {
        public const float DefaultLiveLifetimeSeconds = 120f;
        public const float MinimumLifetimeSeconds = 1f;
        public const float MaximumLifetimeSeconds = 86400f;
        public const int MaximumLootEntries = 24;
    }

    public static class WorldActorCapabilityKindIds
    {
        public const string Dialogue = "dialogue";
        public const string Vendor = "vendor";
        public const string QuestOffer = "quest_offer";
        public const string QuestTurnIn = "quest_turn_in";
        public const string Crafting = "crafting";
        public const string Insurance = "insurance";
        public const string Trainer = "trainer";
        public const string Bank = "bank";
        public const string RecoveryStorage = "recovery_storage";
    }

    public static class WorldActorSpawnKindIds
    {
        public const string Point = "point";
        public const string Group = "group";
        public const string Area = "area";
    }

    public static class WorldActorActivityTierIds
    {
        public const string EventDriven = "event_driven";
        public const string Dormant = "dormant";
        public const string Active = "active";
    }

    public static class WorldInteractionTargetKindIds
    {
        public const string WorldActor = "world_actor";
        public const string Corpse = "corpse";
    }

    public static class WorldInteractionRules
    {
        // The pointer ray from the camera, which zooms out to 30 meters behind the player.
        public const float ClientPointerRange = 100f;
        public const float ClientSpherecastRadius = 0.15f;
        public const float AuthoritativeStartRange = 3f;
        public const float AuthoritativeMaintainRange = 3.5f;
        public const float AuthoritativeEyeHeight = 1.6f;

        public static float DistanceSquaredToBounds(
            float pointX,
            float pointY,
            float pointZ,
            float boundsCenterX,
            float boundsCenterY,
            float boundsCenterZ,
            float boundsSizeX,
            float boundsSizeY,
            float boundsSizeZ)
        {
            var halfX = boundsSizeX * 0.5f;
            var halfY = boundsSizeY * 0.5f;
            var halfZ = boundsSizeZ * 0.5f;
            var closestX = Clamp(pointX, boundsCenterX - halfX, boundsCenterX + halfX);
            var closestY = Clamp(pointY, boundsCenterY - halfY, boundsCenterY + halfY);
            var closestZ = Clamp(pointZ, boundsCenterZ - halfZ, boundsCenterZ + halfZ);
            var deltaX = pointX - closestX;
            var deltaY = pointY - closestY;
            var deltaZ = pointZ - closestZ;
            return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    [Serializable]
    public sealed class WorldActorCatalogAuthoringDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public WorldActorFactionAuthoringEntry[] Factions;
        public WorldActorPresentationAuthoringEntry[] PresentationArchetypes;
        public WorldActorActivityProfileAuthoringEntry[] ActivityProfiles;
        public WorldActorRespawnProfileAuthoringEntry[] RespawnProfiles;
        public WorldActorCombatProfileAuthoringEntry[] CombatProfiles;
        public WorldActorBrainProfileAuthoringEntry[] BrainProfiles;
        public WorldActorLocomotionProfileAuthoringEntry[] LocomotionProfiles;
        public WorldActorLootProfileAuthoringEntry[] LootProfiles;
        public WorldActorAuthoringEntry[] Actors;
    }

    [Serializable]
    public sealed class WorldActorSpawnAuthoringDocument
    {
        public int FormatVersion;
        public string WorldId;
        public string ActorCatalogId;
        public WorldActorSpawnPointAuthoringEntry[] SpawnPoints;
        public WorldActorSpawnGroupAuthoringEntry[] SpawnGroups;
        public WorldActorSpawnAreaAuthoringEntry[] SpawnAreas;
        public WorldActorPatrolPathAuthoringEntry[] PatrolPaths;
    }

    [Serializable]
    public sealed class WorldActorFactionAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public string DefaultDisposition;
    }

    [Serializable]
    public sealed class WorldActorPresentationAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public string ActorKind;
    }

    [Serializable]
    public sealed class WorldActorActivityProfileAuthoringEntry
    {
        public string Id;
        public float ActivationRange;
        public float DormancyRange;
        public float ActiveTickRateHz;
        public float DormantTickRateHz;
    }

    [Serializable]
    public sealed class WorldActorRespawnProfileAuthoringEntry
    {
        public string Id;
        public float DelaySeconds;
        public int PopulationLimit;
    }

    // A Mob's health and its one attack.
    [Serializable]
    public sealed class WorldActorCombatProfileAuthoringEntry
    {
        public string Id;
        public int MaximumHealth;
        public string AttackStyle;
        public string AttackDamageType;
        public int MinimumDamage = -1;
        public int MaximumDamage = -1;
        public float AttackIntervalSeconds;
        public float AttackRange;
    }

    // How a Mob picks a fight and when it gives up and walks home.
    [Serializable]
    public sealed class WorldActorBrainProfileAuthoringEntry
    {
        public string Id;
        public string Aggression;
        public float DetectionRange;
        public float LeashRange;
        public float GiveUpSeconds;
    }

    // How a Mob moves: its walk speed and its collision capsule.
    [Serializable]
    public sealed class WorldActorLocomotionProfileAuthoringEntry
    {
        public string Id;
        public float WalkSpeed;
        public float CollisionRadius;
        public float CollisionHeight;
    }

    // Independent loot rolls: each entry drops with its chance and a quantity in its range.
    [Serializable]
    public sealed class WorldActorLootProfileAuthoringEntry
    {
        public string Id;
        public WorldActorLootEntryAuthoringEntry[] Entries;
    }

    [Serializable]
    public sealed class WorldActorLootEntryAuthoringEntry
    {
        public string DefinitionId;
        public int MinimumQuantity;
        public int MaximumQuantity;
        public int ChanceBasisPoints;
    }

    [Serializable]
    public sealed class WorldActorAuthoringEntry
    {
        public string Id;
        public string DisplayName;
        public string Kind;
        public string FactionId;
        public string Disposition;
        public string PresentationArchetypeId;
        public string DamagePolicy;
        public string[] Tags;
        public WorldActorBoundsAuthoringEntry InteractionBounds;
        public WorldActorCapabilityAuthoringEntry[] Capabilities;
        public string ActivityProfileId;
        public string RespawnProfileId;
        public string CorpsePersistenceMode;
        public float CorpseLifetimeSeconds;
        public string CombatProfileId;
        public string BrainProfileId;
        public string LocomotionProfileId;
        public string LootProfileId;
    }

    [Serializable]
    public sealed class WorldActorBoundsAuthoringEntry
    {
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float SizeX;
        public float SizeY;
        public float SizeZ;
    }

    [Serializable]
    public sealed class WorldActorCapabilityAuthoringEntry
    {
        public string Id;
        public string Kind;
        public string DisplayName;
    }

    [Serializable]
    public sealed class WorldActorSpawnPointAuthoringEntry
    {
        public string Id;
        public string ActorId;
        public float X;
        public float Y;
        public float Z;
        public float YawDegrees;
        public string PatrolPathId;
    }

    [Serializable]
    public sealed class WorldActorSpawnGroupAuthoringEntry
    {
        public string Id;
        public string ActorId;
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float YawDegrees;
        public int Rows;
        public int Columns;
        public float Spacing;
        public string PatrolPathId;
    }

    [Serializable]
    public sealed class WorldActorSpawnAreaAuthoringEntry
    {
        public string Id;
        public string ActorId;
        public float MinimumX;
        public float MinimumZ;
        public float MaximumX;
        public float MaximumZ;
        public float Y;
        public float YawDegrees;
        public int Count;
        public int Seed;
        public string PatrolPathId;
    }

    [Serializable]
    public sealed class WorldActorPatrolPathAuthoringEntry
    {
        public string Id;
        public WorldActorPatrolPointAuthoringEntry[] Points;
    }

    [Serializable]
    public sealed class WorldActorPatrolPointAuthoringEntry
    {
        public float X;
        public float Y;
        public float Z;
        public float WaitSeconds;
    }

    [Serializable]
    public sealed class WorldActorRuntimeDocument
    {
        public int FormatVersion;
        public string CatalogId;
        public string WorldId;
        public string Revision;
        public WorldActorFactionDefinition[] Factions;
        public WorldActorPresentationDefinition[] PresentationArchetypes;
        public WorldActorActivityProfileDefinition[] ActivityProfiles;
        public WorldActorRespawnProfileDefinition[] RespawnProfiles;
        public WorldActorCombatProfileDefinition[] CombatProfiles;
        public WorldActorBrainProfileDefinition[] BrainProfiles;
        public WorldActorLocomotionProfileDefinition[] LocomotionProfiles;
        public WorldActorLootProfileDefinition[] LootProfiles;
        public WorldActorDefinition[] Actors;
        public WorldActorSpawnSourceDefinition[] SpawnSources;
        public WorldActorSpawnInstanceDefinition[] SpawnInstances;
        public WorldActorPatrolPathDefinition[] PatrolPaths;
    }

    [Serializable]
    public sealed class WorldActorFactionDefinition
    {
        public string Id;
        public string DisplayName;
        public string DefaultDisposition;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorPresentationDefinition
    {
        public string Id;
        public string DisplayName;
        public string ActorKind;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorActivityProfileDefinition
    {
        public string Id;
        public float ActivationRange;
        public float DormancyRange;
        public float ActiveTickRateHz;
        public float DormantTickRateHz;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorRespawnProfileDefinition
    {
        public string Id;
        public float DelaySeconds;
        public int PopulationLimit;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorCombatProfileDefinition
    {
        public string Id;
        public int MaximumHealth;
        public string AttackStyle;
        public string AttackDamageType;
        public int MinimumDamage;
        public int MaximumDamage;
        public float AttackIntervalSeconds;
        public float AttackRange;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorBrainProfileDefinition
    {
        public string Id;
        public string Aggression;
        public float DetectionRange;
        public float LeashRange;
        public float GiveUpSeconds;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorLocomotionProfileDefinition
    {
        public string Id;
        public float WalkSpeed;
        public float CollisionRadius;
        public float CollisionHeight;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorLootProfileDefinition
    {
        public string Id;
        public WorldActorLootEntryDefinition[] Entries;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorLootEntryDefinition
    {
        public string DefinitionId;
        public int MinimumQuantity;
        public int MaximumQuantity;
        public int ChanceBasisPoints;
    }

    [Serializable]
    public sealed class WorldActorDefinition
    {
        public string Id;
        public string DisplayName;
        public string Kind;
        public string FactionId;
        public string Disposition;
        public string PresentationArchetypeId;
        public string DamagePolicy;
        public string[] Tags;
        public WorldActorBoundsDefinition InteractionBounds;
        public WorldActorCapabilityDefinition[] Capabilities;
        public string ActivityProfileId;
        public string RespawnProfileId;
        public string CorpsePersistenceMode;
        public float CorpseLifetimeSeconds;
        public string CombatProfileId;
        public string BrainProfileId;
        public string LocomotionProfileId;
        public string LootProfileId;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorBoundsDefinition
    {
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float SizeX;
        public float SizeY;
        public float SizeZ;
    }

    [Serializable]
    public sealed class WorldActorCapabilityDefinition
    {
        public string Id;
        public string Kind;
        public string DisplayName;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorSpawnSourceDefinition
    {
        public string Id;
        public string Kind;
        public string ActorId;
        public int InstanceCount;
        public string PatrolPathId;
        public float OriginX;
        public float OriginY;
        public float OriginZ;
        public float MinimumX;
        public float MinimumZ;
        public float MaximumX;
        public float MaximumZ;
        public float YawDegrees;
        public int Rows;
        public int Columns;
        public float Spacing;
        public int Seed;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorSpawnInstanceDefinition
    {
        public string Id;
        public string SpawnDefinitionId;
        public string ActorId;
        public float X;
        public float Y;
        public float Z;
        public float YawDegrees;
        public string PatrolPathId;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorPatrolPathDefinition
    {
        public string Id;
        public WorldActorPatrolPointDefinition[] Points;
        public string StructuralFingerprint;
    }

    [Serializable]
    public sealed class WorldActorPatrolPointDefinition
    {
        public float X;
        public float Y;
        public float Z;
        public float WaitSeconds;
    }
}
