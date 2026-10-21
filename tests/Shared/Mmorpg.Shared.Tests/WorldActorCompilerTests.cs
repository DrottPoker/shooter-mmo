using Mmorpg.Testing.Content;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Shared.Tests;

public sealed class WorldActorCompilerTests
{
    public static TheoryData<string> CheckedInWorldIds => new(WorldActorTestData.CheckedInWorldIds());

    [Fact]
    public void FixtureCompilesToTheExpectedRuntime()
    {
        var runtime = WorldActorTestData.Compile();

        Assert.Equal(WorldActorDataFormat.Version, runtime.FormatVersion);
        Assert.Equal("core", runtime.CatalogId);
        Assert.Equal(WorldActorTestData.FixtureWorldId, runtime.WorldId);
        Assert.Equal(64, runtime.Revision.Length);
        Assert.Equal(5, runtime.Actors.Length);
        Assert.Equal(6, runtime.SpawnInstances.Length);
    }

    [Theory]
    [MemberData(nameof(CheckedInWorldIds))]
    public void CheckedInWorldAuthoringMatchesItsGeneratedRuntime(string worldId)
    {
        var runtime = WorldActorTestData.CompileWorld(worldId);

        Assert.Equal(worldId, runtime.WorldId);
        Assert.NotEmpty(runtime.SpawnInstances);
        Assert.Equal(
            WorldActorJson.SerializeRuntime(runtime),
            NormalizeLineEndings(WorldActorTestData.RuntimeJsonFor(worldId)));
    }

    [Fact]
    public void DevelopmentWorldTwoPlacesEveryActorKind()
    {
        var runtime = WorldActorTestData.CompileWorld("development-world-2");

        Assert.Equal(5, runtime.Actors.Length);
        Assert.Equal(17, runtime.SpawnInstances.Length);
        Assert.Contains(runtime.SpawnInstances, value => value.ActorId == "mob.feral_alpha");
        Assert.Contains(runtime.SpawnInstances, value => value.ActorId == "resource.iron_vein");
    }

    [Fact]
    public void EquivalentAuthoringOrderProducesSameRevisionAndOutput()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        var expected = WorldActorCompiler.Compile(actors, spawns);

        Array.Reverse(actors.Factions);
        Array.Reverse(actors.PresentationArchetypes);
        Array.Reverse(actors.ActivityProfiles);
        Array.Reverse(actors.RespawnProfiles);
        Array.Reverse(actors.ResourceProfiles);
        Array.Reverse(actors.CombatProfiles);
        Array.Reverse(actors.BrainProfiles);
        Array.Reverse(actors.LocomotionProfiles);
        Array.Reverse(actors.LootProfiles);
        Array.Reverse(actors.Actors);
        foreach (var actor in actors.Actors)
        {
            Array.Reverse(actor.Tags);
        }

        Array.Reverse(spawns.SpawnPoints);
        Array.Reverse(spawns.SpawnGroups);
        Array.Reverse(spawns.SpawnAreas);
        Array.Reverse(spawns.PatrolPaths);
        var reordered = WorldActorCompiler.Compile(actors, spawns);

        Assert.Equal(expected.Revision, reordered.Revision);
        Assert.Equal(
            WorldActorJson.SerializeRuntime(expected),
            WorldActorJson.SerializeRuntime(reordered));
    }

    // The authored capability order is the right-click menu order, so it is content.
    [Fact]
    public void CapabilitiesKeepTheirAuthoredMenuOrder()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        var expected = WorldActorCompiler.Compile(actors, spawns);
        var mira = expected.Actors.Single(value => value.Id == "npc.city_services");

        Assert.Equal(
            ["Talk-to", "Trade", "Bank", "Recover", "Insure", "Quests", "Turn-in", "Craft"],
            mira.Capabilities.Select(value => value.MenuOption));

        Array.Reverse(actors.Actors.Single(value => value.Id == "npc.city_services").Capabilities);
        var reordered = WorldActorCompiler.Compile(actors, spawns);

        Assert.NotEqual(expected.Revision, reordered.Revision);
        Assert.Equal(
            mira.Capabilities.Select(value => value.Id).Reverse(),
            reordered.Actors.Single(value => value.Id == "npc.city_services")
                .Capabilities.Select(value => value.Id));
        WorldActorCompiler.ValidateRuntime(reordered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" A wolf.")]
    [InlineData("A wolf.\nIt howls.")]
    public void ExamineTextMustBeOneTrimmedLine(string? examineText)
    {
        var actors = WorldActorTestData.LoadActors();
        actors.Actors[0].ExamineText = examineText!;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains("examineText", StringComparison.Ordinal));
    }

    [Fact]
    public void ExamineTextAndMenuOptionsHaveLengthLimits()
    {
        var actors = WorldActorTestData.LoadActors();
        var npc = actors.Actors.First(value => value.Capabilities.Length > 0);
        npc.ExamineText = new string('e', WorldActorTextRules.MaximumExamineTextLength);
        npc.Capabilities[0].MenuOption = new string('m', WorldActorTextRules.MaximumMenuOptionLength);
        _ = WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns());

        npc.ExamineText += "e";
        npc.Capabilities[0].MenuOption += "m";
        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains("examineText", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("menuOption", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCapabilityNeedsAMenuOption()
    {
        var actors = WorldActorTestData.LoadActors();
        actors.Actors.First(value => value.Capabilities.Length > 0).Capabilities[0].MenuOption = " ";

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains("menuOption", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidTagsAndPopulationLimitsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        actors.Actors[0].Tags = ["wild", "wild"];
        actors.RespawnProfiles[0].PopulationLimit = 0;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(exception.Errors, value => value.Contains("tags", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            value => value.Contains("populationLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void NpcComposesCapabilitiesWithoutActorSubclass()
    {
        var runtime = WorldActorTestData.Compile();
        var actor = runtime.Actors.Single(value => value.Id == "npc.city_services");

        Assert.Equal(WorldActorKindIds.Npc, actor.Kind);
        Assert.Equal(8, actor.Capabilities.Length);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.Dialogue);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.Vendor);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.QuestOffer);
        Assert.Contains(actor.Capabilities, value => value.Kind == WorldActorCapabilityKindIds.Crafting);
        Assert.All(runtime.Actors, value => Assert.IsType<WorldActorDefinition>(value));
    }

    [Fact]
    public void MobCorpseLifetimeAndPersistenceAreContentControlledPerDefinition()
    {
        var runtime = WorldActorTestData.Compile();
        var normalMob = runtime.Actors.Single(value => value.Id == "mob.feral_wolf");
        var bossMob = runtime.Actors.Single(value => value.Id == "mob.feral_alpha");

        Assert.Equal(WorldActorCorpsePersistenceModeIds.Live, normalMob.CorpsePersistenceMode);
        Assert.Equal(120f, normalMob.CorpseLifetimeSeconds);
        Assert.Equal(WorldActorCorpsePersistenceModeIds.Durable, bossMob.CorpsePersistenceMode);
        Assert.Equal(600f, bossMob.CorpseLifetimeSeconds);
    }

    [Fact]
    public void MobsReferenceTheirCombatBrainLocomotionAndLootProfiles()
    {
        var runtime = WorldActorTestData.Compile();
        var wolf = runtime.Actors.Single(value => value.Id == "mob.feral_wolf");
        var guard = runtime.Actors.Single(value => value.Id == "npc.city_guard");
        var combat = runtime.CombatProfiles.Single(value => value.Id == wolf.CombatProfileId);
        var brain = runtime.BrainProfiles.Single(value => value.Id == wolf.BrainProfileId);
        var locomotion = runtime.LocomotionProfiles.Single(value => value.Id == wolf.LocomotionProfileId);
        var loot = runtime.LootProfiles.Single(value => value.Id == wolf.LootProfileId);

        Assert.Equal(
            (50, CombatStyleIds.Melee, DamageTypeIds.Normal, 4, 9, 1.8f, 1.5f),
            (combat.MaximumHealth, combat.AttackStyle, combat.AttackDamageType, combat.MinimumDamage,
                combat.MaximumDamage, combat.AttackIntervalSeconds, combat.AttackRange));
        Assert.Equal(
            (WorldActorAggressionIds.Aggressive, 8f, 30f, 10f),
            (brain.Aggression, brain.DetectionRange, brain.LeashRange, brain.GiveUpSeconds));
        Assert.Equal(4.5f, locomotion.WalkSpeed);
        Assert.Equal(
            ["material.wolf_pelt", "ammunition.bronze_arrow"],
            loot.Entries.Select(entry => entry.DefinitionId));
        Assert.Equal(
            (string.Empty, string.Empty, string.Empty, string.Empty),
            (guard.CombatProfileId, guard.BrainProfileId, guard.LocomotionProfileId, guard.LootProfileId));
    }

    [Theory]
    [InlineData("combat-health", "combatProfiles[0].maximumHealth")]
    [InlineData("combat-style", "combatProfiles[0].attackStyle")]
    [InlineData("combat-damage-type", "combatProfiles[0].attackDamageType")]
    [InlineData("combat-damage", "combatProfiles[0].minimumDamage and maximumDamage")]
    [InlineData("combat-interval", "combatProfiles[0].attackIntervalSeconds")]
    [InlineData("combat-range", "combatProfiles[0].attackRange")]
    [InlineData("brain-aggression", "brainProfiles[0].aggression")]
    [InlineData("brain-detection", "brainProfiles[0].detectionRange")]
    [InlineData("brain-leash", "brainProfiles[0].leashRange")]
    [InlineData("brain-give-up", "brainProfiles[0].giveUpSeconds")]
    [InlineData("locomotion-speed", "locomotionProfiles[0].walkSpeed")]
    [InlineData("locomotion-height", "locomotionProfiles[0].collisionHeight")]
    [InlineData("loot-empty", "lootProfiles[0].entries must contain")]
    [InlineData("loot-chance", "lootProfiles[0].entries[0].chanceBasisPoints")]
    [InlineData("loot-quantity", "lootProfiles[0].entries[0].minimumQuantity and maximumQuantity")]
    [InlineData("loot-duplicate", "lootProfiles[0].entries[1].definitionId duplicates id")]
    [InlineData("mob-without-combat", ".combatProfileId is required")]
    [InlineData("mob-unknown-loot", ".lootProfileId references missing id")]
    [InlineData("npc-with-brain", ".brainProfileId must be empty")]
    [InlineData("missing-combat-profiles", "combatProfiles is required")]
    public void InvalidMobProfilesFailValidation(string mutation, string expectedError)
    {
        var actors = WorldActorTestData.LoadActors();
        var wolf = actors.Actors.Single(value => value.Id == "mob.feral_wolf");
        var guard = actors.Actors.Single(value => value.Id == "npc.city_guard");
        var combat = actors.CombatProfiles[0];
        var brain = actors.BrainProfiles[0];
        var locomotion = actors.LocomotionProfiles[0];
        var loot = actors.LootProfiles[0];
        switch (mutation)
        {
            case "combat-health":
                combat.MaximumHealth = 0;
                break;
            case "combat-style":
                combat.AttackStyle = "siege";
                break;
            case "combat-damage-type":
                combat.AttackDamageType = "lightning";
                break;
            case "combat-damage":
                combat.MinimumDamage = combat.MaximumDamage + 1;
                break;
            case "combat-interval":
                combat.AttackIntervalSeconds = 0.1f;
                break;
            case "combat-range":
                combat.AttackRange = 0f;
                break;
            case "brain-aggression":
                brain.Aggression = "curious";
                break;
            case "brain-detection":
                brain.DetectionRange = 0f;
                break;
            case "brain-leash":
                brain.LeashRange = brain.DetectionRange;
                break;
            case "brain-give-up":
                brain.GiveUpSeconds = 0f;
                break;
            case "locomotion-speed":
                locomotion.WalkSpeed = float.NaN;
                break;
            case "locomotion-height":
                locomotion.CollisionHeight = locomotion.CollisionRadius * 2f;
                break;
            case "loot-empty":
                loot.Entries = [];
                break;
            case "loot-chance":
                loot.Entries[0].ChanceBasisPoints = 0;
                break;
            case "loot-quantity":
                loot.Entries[0].MinimumQuantity = 0;
                break;
            case "loot-duplicate":
                loot.Entries[1].DefinitionId = loot.Entries[0].DefinitionId;
                break;
            case "mob-without-combat":
                wolf.CombatProfileId = string.Empty;
                break;
            case "mob-unknown-loot":
                wolf.LootProfileId = "mob.missing";
                break;
            case "npc-with-brain":
                guard.BrainProfileId = brain.Id;
                break;
            case "missing-combat-profiles":
                actors.CombatProfiles = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(
            exception.Errors,
            value => value.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void MobWithoutLootProfileCompiles()
    {
        var actors = WorldActorTestData.LoadActors();
        actors.Actors.Single(value => value.Id == "mob.feral_wolf").LootProfileId = string.Empty;

        var runtime = WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns());

        Assert.Empty(runtime.Actors.Single(value => value.Id == "mob.feral_wolf").LootProfileId);
    }

    [Fact]
    public void CommittedLootReferencesFitTheCompiledItemCatalog()
    {
        WorldActorItemReferences.Validate(WorldActorTestData.Compile(), ItemCatalogTestData.Compile());
    }

    [Theory]
    [InlineData("unknown", "references an unknown item definition")]
    [InlineData("quest", "cannot drop a quest item")]
    [InlineData("stack", "drops more than one stack")]
    public void LootThatTheItemCatalogCannotHoldFailsValidation(string mutation, string expectedError)
    {
        var runtime = WorldActorTestData.Compile();
        var entry = runtime.LootProfiles.Single(value => value.Id == "mob.feral_wolf").Entries[0];
        var items = ItemCatalogTestData.Compile();
        switch (mutation)
        {
            case "unknown":
                entry.DefinitionId = "material.dragon_scale";
                break;
            case "quest":
                entry.DefinitionId = items.Definitions
                    .First(value => value.Category == ItemCategoryIds.QuestItem)
                    .Id;
                break;
            case "stack":
                entry.MaximumQuantity = items.Definitions
                    .Single(value => value.Id == entry.DefinitionId)
                    .MaximumStackSize + 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorItemReferences.Validate(runtime, items));

        Assert.Contains(
            exception.Errors,
            value => value.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeValidationRejectsATamperedMobProfile()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.CombatProfiles[0].MaximumHealth++;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.ValidateRuntime(runtime));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("runtime combat profile", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidMobCorpseSettingsAndNpcCorpseSettingsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var mob = actors.Actors.Single(value => value.Id == "mob.feral_wolf");
        var npc = actors.Actors.Single(value => value.Id == "npc.city_guard");
        mob.CorpsePersistenceMode = "unsupported";
        mob.CorpseLifetimeSeconds = 0f;
        npc.CorpsePersistenceMode = WorldActorCorpsePersistenceModeIds.Live;
        npc.CorpseLifetimeSeconds = 120f;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("corpsePersistenceMode", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            value => value.Contains("corpseLifetimeSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateContentAndSpawnIdsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[1].Id = actors.Actors[0].Id;
        spawns.SpawnGroups[0].Id = spawns.SpawnPoints[0].Id;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.Contains(exception.Errors, value => value.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingReferencesFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[0].FactionId = "missing.faction";
        spawns.SpawnPoints[0].ActorId = "missing.actor";
        spawns.SpawnAreas[0].PatrolPathId = "missing.path";

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.True(exception.Errors.Count(value => value.Contains("missing id", StringComparison.Ordinal)) >= 3);
    }

    [Fact]
    public void InvalidTransformAndBoundsFailValidation()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[0].InteractionBounds.SizeX = 0f;
        spawns.SpawnPoints[0].X = float.NaN;
        spawns.SpawnAreas[0].MaximumZ = spawns.SpawnAreas[0].MinimumZ;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.Contains(exception.Errors, value => value.Contains("sizeX", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("spawnPoints[0].x", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, value => value.Contains("positive X and Z size", StringComparison.Ordinal));
    }

    [Fact]
    public void WorldCoordinatesAndInteractionBoundsAreExplicitlyBounded()
    {
        var actors = WorldActorTestData.LoadActors();
        var spawns = WorldActorTestData.LoadSpawns();
        actors.Actors[0].InteractionBounds.SizeX = 1_000_001f;
        spawns.SpawnPoints[0].X = 1_000_001f;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, spawns));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("supported bound", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            value => value.Contains("supported World bounds", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcessiveSpawnDimensionsFailBeforeInstanceExpansion()
    {
        var spawns = WorldActorTestData.LoadSpawns();
        spawns.SpawnGroups[0].Rows = int.MaxValue;
        spawns.SpawnGroups[0].Columns = int.MaxValue;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(WorldActorTestData.LoadActors(), spawns));

        Assert.Contains(
            exception.Errors,
            value => value.Contains("instances", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unsupported", "invulnerable")]
    [InlineData("npc", "damageable")]
    public void UnsupportedKindOrDamageableNpcFailsValidation(string kind, string damagePolicy)
    {
        var actors = WorldActorTestData.LoadActors();
        var actor = actors.Actors.Single(value => value.Id == "npc.city_guard");
        actor.Kind = kind;
        actor.DamagePolicy = damagePolicy;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.Compile(actors, WorldActorTestData.LoadSpawns()));

        Assert.NotEmpty(exception.Errors);
    }

    [Fact]
    public void UnknownJsonMemberAndDuplicatePropertyFailStrictParsing()
    {
        var unknown = WorldActorTestData.ActorCatalogJson.Replace(
            "\"catalogId\": \"core\",",
            "\"catalogId\": \"core\",\n  \"unknown\": true,",
            StringComparison.Ordinal);
        var duplicate = WorldActorTestData.SpawnAuthoringJson.Replace(
            "\"worldId\": \"development-world-1\",",
            "\"worldId\": \"development-world-1\",\n  \"worldId\": \"duplicate\",",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => WorldActorJson.DeserializeCatalog(unknown));
        Assert.Throws<InvalidDataException>(() => WorldActorJson.DeserializeSpawns(duplicate));
    }

    [Fact]
    public void ClosestPointDistanceUsesServerOwnedBounds()
    {
        var distanceSquared = WorldInteractionRules.DistanceSquaredToBounds(
            0f,
            0f,
            0f,
            3f,
            1f,
            0f,
            2f,
            2f,
            2f);

        Assert.Equal(4f, distanceSquared, 4);
    }

    [Fact]
    public void RuntimeInstanceMustMatchItsDeterministicSpawnSource()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.SpawnInstances[0].X += 0.5f;

        var exception = Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.ValidateRuntime(runtime));

        Assert.Contains(
            exception.Errors,
            value => value.Contains(
                "deterministic source placement",
                StringComparison.Ordinal));
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
