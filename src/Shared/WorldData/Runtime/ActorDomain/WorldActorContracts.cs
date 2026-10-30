using System;

namespace Mmorpg.WorldData.Actors
{
    public static class WorldActorDataFormat
    {
        public const int Version = 5;
    }

    // Collision bounds block character movement. Only resource nodes have them: they never move,
    // and a depleted node keeps its footprint. NPCs and Mobs leave every value at zero.
    public static class WorldActorCollisionRules
    {
        public const float MaximumSize = 16f;
    }

    // Player-facing text of an actor: the Examine description and the right-click menu option
    // of each capability, such as "Trade" or "Mine". Both fit the realtime actor spawn.
    public static class WorldActorTextRules
    {
        public const int MaximumExamineTextLength = 200;
        public const int MaximumMenuOptionLength = 32;
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

        // A Mob corpse's presentation key: corpse.mob, the remains of any Mob, followed by the rest
        // of the Mob's presentation archetype, so the client lays down the Mob's own model, such as
        // corpse.mob.feral_wolf for mob.feral_wolf. Every such key takes whole items only.
        public const string MobCorpsePresentationKey = "corpse.mob";

        public static string MobCorpsePresentationKeyFor(string presentationArchetypeId)
        {
            return presentationArchetypeId != null
                && presentationArchetypeId.Length > MobArchetypePrefix.Length
                && presentationArchetypeId.StartsWith(MobArchetypePrefix, StringComparison.Ordinal)
                ? "corpse." + presentationArchetypeId
                : MobCorpsePresentationKey;
        }

        public static bool IsMobCorpsePresentationKey(string presentationKey)
        {
            if (string.Equals(presentationKey, MobCorpsePresentationKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (presentationKey == null
                || presentationKey.Length <= MobCorpsePresentationKey.Length + 1
                || presentationKey.Length > 128
                || !presentationKey.StartsWith(MobCorpsePresentationKey + ".", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var character in presentationKey)
            {
                var allowed = (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.'
                    || character == '_'
                    || character == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        private const string MobArchetypePrefix = "mob.";
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

    // How a Mob moves: its walk speed, which it chases and walks home at, its slower patrol speed
    // on its routine, and its collision capsule.
    [Serializable]
    public sealed class WorldActorLocomotionProfileAuthoringEntry
    {
        public string Id;
        public float WalkSpeed;
        public float PatrolSpeed;
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
        public string ExamineText;
        public string Kind;
        public string FactionId;
        public string Disposition;
        public string PresentationArchetypeId;
        public string DamagePolicy;
        public string[] Tags;
        public WorldActorBoundsAuthoringEntry InteractionBounds;
        public WorldActorBoundsAuthoringEntry CollisionBounds;
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

    // MenuOption is the verb the right-click menu shows before the actor's name, such as
    // "Trade". ProfileId names the vendor profile of a vendor capability or the resource
    // profile of a gathering capability, and is empty for every other kind. The authored order
    // is the menu order, and the first capability is the left-click action.
    [Serializable]
    public sealed class WorldActorCapabilityAuthoringEntry
    {
        public string Id;
        public string Kind;
        public string DisplayName;
        public string MenuOption;
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
        public float PatrolSpeed;
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
        public string ExamineText;
        public string Kind;
        public string FactionId;
        public string Disposition;
        public string PresentationArchetypeId;
        public string DamagePolicy;
        public string[] Tags;
        public WorldActorBoundsDefinition InteractionBounds;
        public WorldActorBoundsDefinition CollisionBounds;
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
        public string MenuOption;
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
