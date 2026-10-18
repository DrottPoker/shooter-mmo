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

        // An interactable world object that yields a resource, such as an ore vein.
        public const string ResourceNode = "resource_node";
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
        public const string Gathering = "gathering";
    }

    public static class WorldActorResourceRules
    {
        public const float MinimumCycleSeconds = 0.5f;
        public const float MaximumCycleSeconds = 60f;
        public const int MaximumCharges = 1000;
        public const float MinimumRespawnSeconds = 1f;
        public const float MaximumRespawnSeconds = 86400f;
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
        public WorldActorResourceProfileAuthoringEntry[] ResourceProfiles;
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

    // What one gathering cycle at a resource node yields and requires. The node shares its
    // charges between every gatherer and returns after the respawn delay once depleted.
    [Serializable]
    public sealed class WorldActorResourceProfileAuthoringEntry
    {
        public string Id;
        public string RequiredToolKind;
        public string YieldDefinitionId;
        public int YieldQuantity;
        public float CycleSeconds;
        public int Charges;
        public float RespawnSeconds;
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

    // ProfileId names the vendor profile of a vendor capability or the resource profile of a
    // gathering capability, and is empty for every other kind.
    [Serializable]
    public sealed class WorldActorCapabilityAuthoringEntry
    {
        public string Id;
        public string Kind;
        public string DisplayName;
        public string ProfileId;
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
        public WorldActorResourceProfileDefinition[] ResourceProfiles;
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
    public sealed class WorldActorResourceProfileDefinition
    {
        public string Id;
        public string RequiredToolKind;
        public string YieldDefinitionId;
        public int YieldQuantity;
        public float CycleSeconds;
        public int Charges;
        public float RespawnSeconds;
        public string StructuralFingerprint;
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
        public string ProfileId;
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
