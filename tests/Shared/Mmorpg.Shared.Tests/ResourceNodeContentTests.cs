using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

// Resource nodes, gathering capabilities, resource profiles, and the references from actor
// content to vendor profiles, tool kinds, and item definitions.
public sealed class ResourceNodeContentTests
{
    [Fact]
    public void IronVeinIsAResourceNodeWithOneGatheringCapability()
    {
        var runtime = WorldActorTestData.Compile();
        var vein = runtime.Actors.Single(value => value.Id == "resource.iron_vein");
        var profile = runtime.ResourceProfiles.Single(value => value.Id == "resource.iron_vein");

        Assert.Equal(WorldActorKindIds.ResourceNode, vein.Kind);
        Assert.Equal(WorldActorDamagePolicyIds.Invulnerable, vein.DamagePolicy);
        var capability = Assert.Single(vein.Capabilities);
        Assert.Equal(WorldActorCapabilityKindIds.Gathering, capability.Kind);
        Assert.Equal(profile.Id, capability.ProfileId);
        Assert.Equal("pickaxe", profile.RequiredToolKind);
        Assert.Equal("material.iron_ore", profile.YieldDefinitionId);
        Assert.Equal(1, profile.YieldQuantity);
        Assert.Equal(3f, profile.CycleSeconds);
        Assert.Equal(5, profile.Charges);
        Assert.Equal(30f, profile.RespawnSeconds);
    }

    [Fact]
    public void OakTreeIsChoppedWithAWoodcuttingAxeForOakLogs()
    {
        var runtime = WorldActorTestData.Compile();
        var tree = runtime.Actors.Single(value => value.Id == "resource.oak_tree");
        var profile = runtime.ResourceProfiles.Single(value => value.Id == "resource.oak_tree");
        var items = ItemCatalogTestData.Compile();

        Assert.Equal(WorldActorKindIds.ResourceNode, tree.Kind);
        Assert.Equal(WorldActorDamagePolicyIds.Invulnerable, tree.DamagePolicy);
        var capability = Assert.Single(tree.Capabilities);
        Assert.Equal(WorldActorCapabilityKindIds.Gathering, capability.Kind);
        Assert.Equal("Chop down", capability.MenuOption);
        Assert.Equal(profile.Id, capability.ProfileId);
        Assert.Equal("woodcutting_axe", profile.RequiredToolKind);
        Assert.Equal("material.oak_log", profile.YieldDefinitionId);
        Assert.Equal(1, profile.YieldQuantity);
        Assert.Equal(3f, profile.CycleSeconds);
        Assert.Equal(5, profile.Charges);
        Assert.Equal(30f, profile.RespawnSeconds);

        // Only the woodcutting axe chops, so a pickaxe in the Tool slot does not.
        Assert.Equal(
            ["tool.woodcutting_axe"],
            items.Definitions
                .Where(definition => definition.ToolKinds.Contains("woodcutting_axe"))
                .Select(definition => definition.Id));
    }

    [Fact]
    public void CheckedInContentPassesItemReferenceValidation()
    {
        var items = ItemCatalogTestData.Compile();

        WorldActorItemReferences.Validate(WorldActorTestData.Compile(), items);
        foreach (var worldId in WorldActorTestData.CheckedInWorldIds())
        {
            WorldActorItemReferences.Validate(WorldActorTestData.CompileWorld(worldId), items);
        }
    }

    // A placed node blocks movement with its collision bounds, offset from its position and
    // aligned to the World axes whatever its yaw; NPCs and Mobs add no box.
    [Fact]
    public void EveryResourceNodeInstanceGetsOneAxisAlignedCollisionBox()
    {
        var runtime = WorldActorTestData.CompileWorld("development-world-2");

        var boxes = WorldActorCollision.Boxes(runtime);

        Assert.Equal(
            runtime.SpawnInstances
                .Where(instance => instance.ActorId.StartsWith("resource.", StringComparison.Ordinal))
                .Select(instance => WorldActorCollision.SourcePathPrefix + instance.Id)
                .Order(StringComparer.Ordinal),
            boxes.Select(box => box.SourcePath));
        var oak = boxes.Single(box => box.SourcePath == "Actors/development_world_2.grove_oak_east:0000");
        Assert.Equal(48f, oak.CenterX);
        Assert.Equal(1.2f, oak.CenterY);
        Assert.Equal(-88f, oak.CenterZ);
        Assert.Equal(0.5f, oak.HalfExtentX);
        Assert.Equal(1.2f, oak.HalfExtentY);
        Assert.Equal(0.5f, oak.HalfExtentZ);
    }

    [Fact]
    public void ResourceNodeNeedsPositiveCollisionBounds()
    {
        var actors = WorldActorTestData.LoadActors();
        Vein(actors).CollisionBounds.SizeY = 0f;

        AssertInvalid(actors, ".collisionBounds.sizeY must be finite, positive");
    }

    [Fact]
    public void OnlyResourceNodesHaveCollisionBounds()
    {
        var actors = WorldActorTestData.LoadActors();
        var mira = actors.Actors.Single(value => value.Id == "npc.city_services");
        mira.CollisionBounds.SizeX = 1f;

        AssertInvalid(actors, "must be zero for an NPC or Mob; only resource nodes collide.");

        mira.CollisionBounds = null;
        AssertInvalid(actors, "collisionBounds is required.");
    }

    [Fact]
    public void ResourceNodeNeedsExactlyOneGatheringCapability()
    {
        var actors = WorldActorTestData.LoadActors();
        Vein(actors).Capabilities =
        [
            .. Vein(actors).Capabilities,
            new WorldActorCapabilityAuthoringEntry
            {
                Id = "resource.talk",
                Kind = WorldActorCapabilityKindIds.Dialogue,
                DisplayName = "Talk",
                ProfileId = string.Empty
            }
        ];

        AssertInvalid(actors, "exactly one gathering capability");
    }

    [Fact]
    public void NpcCannotOfferGathering()
    {
        var actors = WorldActorTestData.LoadActors();
        var npc = actors.Actors.Single(value => value.Id == "npc.city_guard");
        npc.Capabilities =
        [
            .. npc.Capabilities,
            new WorldActorCapabilityAuthoringEntry
            {
                Id = "guard.mine",
                Kind = WorldActorCapabilityKindIds.Gathering,
                DisplayName = "Mine",
                ProfileId = "resource.iron_vein"
            }
        ];

        AssertInvalid(actors, "gathering is only available on a resource node");
    }

    [Fact]
    public void GatheringNeedsAKnownResourceProfile()
    {
        var actors = WorldActorTestData.LoadActors();
        Vein(actors).Capabilities[0].ProfileId = "resource.missing";

        AssertInvalid(actors, "references missing id 'resource.missing'");
    }

    [Fact]
    public void VendorNeedsAProfileAndOtherCapabilitiesMustNotHaveOne()
    {
        var actors = WorldActorTestData.LoadActors();
        var npc = actors.Actors.Single(value => value.Id == "npc.city_services");
        npc.Capabilities.Single(value => value.Kind == WorldActorCapabilityKindIds.Vendor).ProfileId = string.Empty;
        npc.Capabilities.Single(value => value.Kind == WorldActorCapabilityKindIds.Bank).ProfileId = "vendor.city_general_store";

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains("profileId is required", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("profileId must be empty", StringComparison.Ordinal));
    }

    [Fact]
    public void ResourceProfileValuesAreBounded()
    {
        var actors = WorldActorTestData.LoadActors();
        var profile = actors.ResourceProfiles.Single(value => value.Id == "resource.iron_vein");
        profile.CycleSeconds = 0f;
        profile.Charges = 0;
        profile.RespawnSeconds = float.NaN;
        profile.YieldQuantity = 0;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains(".cycleSeconds", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains(".charges", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains(".respawnSeconds", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains(".yieldQuantity", StringComparison.Ordinal));
    }

    [Fact]
    public void ResourceNodeCannotFollowAPatrolPath()
    {
        var spawns = WorldActorTestData.LoadSpawns();
        spawns.SpawnPoints.Single(value => value.ActorId == "resource.iron_vein").PatrolPathId =
            spawns.PatrolPaths[0].Id;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(WorldActorTestData.LoadActors(), spawns));

        Assert.Contains(exception.Errors, value => value.Contains("only be assigned to a Mob", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemReferencesRejectUnknownVendorToolKindAndYield()
    {
        var runtime = WorldActorTestData.Compile();
        var items = ItemCatalogTestData.Compile();
        items.Vendors = [];
        items.ToolKinds = [];
        items.Definitions = items.Definitions
            .Where(definition => definition.Id != "material.iron_ore")
            .ToArray();

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorItemReferences.Validate(runtime, items));

        Assert.Contains(exception.Errors, value => value.Contains("unknown vendor profile", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("unknown tool kind", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("unknown item definition", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemReferencesRejectAProtectedYieldAndAnOversizedYield()
    {
        var runtime = WorldActorTestData.Compile();
        var items = ItemCatalogTestData.Compile();
        var ore = items.Definitions.Single(definition => definition.Id == "material.iron_ore");
        ore.DefaultPolicies = [ItemPolicyIds.ProtectedOnDeath];
        ore.MaximumStackSize = 0;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorItemReferences.Validate(runtime, items));

        Assert.Contains(exception.Errors, value => value.Contains("default policies", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("more than one stack", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolKindWithoutAnyToolDefinitionIsRejected()
    {
        var runtime = WorldActorTestData.Compile();
        var items = ItemCatalogTestData.Compile();
        foreach (var definition in items.Definitions)
        {
            definition.ToolKinds = [];
        }

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorItemReferences.Validate(runtime, items));

        Assert.Contains(exception.Errors, value => value.Contains("which no item definition provides", StringComparison.Ordinal));
    }

    private static WorldActorAuthoringEntry Vein(WorldActorCatalogAuthoringDocument actors)
    {
        return actors.Actors.Single(value => value.Id == "resource.iron_vein");
    }

    private static void AssertInvalid(WorldActorCatalogAuthoringDocument actors, string expectedError)
    {
        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));
        Assert.Contains(exception.Errors, value => value.Contains(expectedError, StringComparison.Ordinal));
    }
}
