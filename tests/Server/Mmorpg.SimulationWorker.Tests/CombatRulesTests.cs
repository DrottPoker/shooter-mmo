using System.Text.Json;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class CombatRulesTests
{
    private static string ContentRulesPath => Path.Combine(RepositoryPaths.Content, "combat", "combat-rules.json");

    [Fact]
    public void TheCheckedInRulesLoadAndReachTheWorkerOutput()
    {
        var content = CombatRulesLoader.Load(ContentRulesPath);
        var output = CombatRulesLoader.Load(Path.Combine(AppContext.BaseDirectory, SimulationWorkerConfig.DefaultCombatRulesPath));

        Assert.Equal(100, content.PlayerMaximumHealth);
        Assert.Equal(100, content.PlayerMaximumMana);
        Assert.Equal(5f, content.RespawnDelaySeconds);
        Assert.Equal(5f, content.CombatTimeoutSeconds);
        Assert.Equal(1, content.HealthRegenerationPerSecond);
        Assert.Equal(1, content.ManaRegenerationPerSecond);
        Assert.Equal(1f, content.EquipmentLockoutSeconds);
        Assert.Equal(5f, content.AmmunitionBatchIntervalSeconds);
        Assert.True(content.TryGetRuleSet("mvp-open-risk", out var openRisk));
        Assert.True(openRisk.PlayerVersusPlayer);
        Assert.Equal(content.RuleSets, output.RuleSets);
    }

    // GameService names each Shard's rule set, and the worker stops on one it cannot enforce.
    [Fact]
    public void EveryConfiguredShardRuleSetIsDefined()
    {
        var rules = CombatRulesLoader.Load(ContentRulesPath);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src",
            "Server",
            "GameService",
            "Config",
            "appsettings.json")));
        var shards = settings.RootElement
            .GetProperty("Simulation")
            .GetProperty("Topology")
            .GetProperty("Shards")
            .EnumerateArray()
            .Select(shard => shard.GetProperty("RuleSet").GetString()!)
            .ToArray();

        Assert.NotEmpty(shards);
        Assert.All(shards, ruleSet => Assert.True(rules.TryGetRuleSet(ruleSet, out _), ruleSet));
    }

    [Theory]
    [InlineData("""{"formatVersion":1,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":3,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":0,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":-1,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":-1,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":101,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":101,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":-1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1,"regenerationPercentPerSecond":2},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":0.1},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"b","playerVersusPlayer":true},{"id":"a","playerVersusPlayer":false}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true},{"id":"a","playerVersusPlayer":false}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"Open Risk","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}],"extra":1}""")]
    [InlineData("""{"formatVersion":2,"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    [InlineData("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":100,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":1,"manaRegenerationPerSecond":1,"equipmentLockoutSeconds":1,},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""")]
    public void InvalidRulesAreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => CombatRulesLoader.Parse(json));
    }

    // A world without magic leaves the mana at 0, and nothing regenerates when the rates are 0.
    [Fact]
    public void ManaAndRegenerationMayBeZero()
    {
        var rules = CombatRulesLoader.Parse("""{"formatVersion":2,"player":{"maximumHealth":100,"maximumMana":0,"respawnDelaySeconds":5,"combatTimeoutSeconds":5,"healthRegenerationPerSecond":0,"manaRegenerationPerSecond":0,"equipmentLockoutSeconds":1},"ammunition":{"batchIntervalSeconds":5},"ruleSets":[{"id":"a","playerVersusPlayer":true}]}""");

        Assert.Equal(0, rules.PlayerMaximumMana);
        Assert.Equal(0, rules.HealthRegenerationPerSecond);
        Assert.Equal(0, rules.ManaRegenerationPerSecond);
    }

    [Fact]
    public void ShardRulesAreUnknownUntilTheRegistrySetsThem()
    {
        var shardRules = new SimulationShardRules();

        Assert.False(shardRules.IsKnown);
        Assert.False(shardRules.Current.PlayerVersusPlayer);
        shardRules.Set(new ShardRuleSet("mvp-open-risk", true));
        Assert.True(shardRules.IsKnown);
        Assert.True(shardRules.Current.PlayerVersusPlayer);
    }
}

public sealed class CombatMathTests
{
    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(10, 5, 11)]
    [InlineData(18, 5, 19)]
    [InlineData(9, 5, 9)]
    [InlineData(30, 5, 32)]
    [InlineData(100, 1000, 1100)]
    public void AmmunitionAddsItsBonusRoundedHalfUp(int roll, int bonusPercent, int expected)
    {
        var damage = CombatDamageCalculator.Calculate(roll, roll, "normal", bonusPercent, new FixedRoll());

        Assert.Equal(expected, damage.Amount);
        Assert.Equal("normal", damage.DamageType);
    }

    [Fact]
    public void TheRollStaysWithinTheAttacksBounds()
    {
        var random = new SeededCombatRandom(42);
        var rolls = Enumerable.Range(0, 2000)
            .Select(_ => CombatDamageCalculator.Calculate(10, 18, "fire", 0, random).Amount)
            .ToArray();

        Assert.Equal(10, rolls.Min());
        Assert.Equal(18, rolls.Max());
    }

    [Theory]
    [InlineData(-1, 5, "normal", 0)]
    [InlineData(10, 5, "normal", 0)]
    [InlineData(1, 5, "poison", 0)]
    [InlineData(1, 5, "normal", -1)]
    [InlineData(1, 5, "normal", 1001)]
    public void InvalidDamageDataIsRejected(int minimum, int maximum, string damageType, int bonus)
    {
        Assert.Throws<ArgumentException>(() => CombatDamageCalculator.Calculate(
            minimum,
            maximum,
            damageType,
            bonus,
            new FixedRoll()));
    }

    [Theory]
    [InlineData(2.4f, 72u)]
    [InlineData(1.8f, 54u)]
    [InlineData(0.5f, 15u)]
    [InlineData(5f, 150u)]
    [InlineData(0f, 0u)]
    public void SecondsBecomeWholeTicks(float seconds, uint ticks)
    {
        Assert.Equal(ticks, CombatTicks.FromSeconds(seconds, 30));
    }

    [Theory]
    [InlineData(10f, 30f, 10u)]
    [InlineData(0.1f, 30f, 1u)]
    [InlineData(0f, 30f, 1u)]
    [InlineData(10f, 0f, 0u)]
    public void ArrowsNeedAtLeastOneTickOfFlight(float distance, float speed, uint ticks)
    {
        Assert.Equal(ticks, CombatTicks.FlightTicks(distance, speed, 30));
    }

    [Fact]
    public void TickComparisonsSurviveTheWrap()
    {
        Assert.True(CombatTicks.IsReached(5, 5));
        Assert.True(CombatTicks.IsReached(6, 5));
        Assert.False(CombatTicks.IsReached(4, 5));
        Assert.True(CombatTicks.IsReached(2, uint.MaxValue - 1));
        Assert.False(CombatTicks.IsReached(uint.MaxValue - 1, 2));
    }

    private sealed class FixedRoll : ICombatRandom
    {
        public int NextInclusive(int minimum, int maximum)
        {
            return minimum;
        }
    }
}

public sealed class CombatLoadoutTests
{
    private static readonly CombatItemCatalog Catalog = new(ItemCatalogTestData.Compile(), 30);

    [Fact]
    public void TheTrainingShortbowFiresBronzeArrows()
    {
        var arrows = Equipped("ammunition", "ammunition.bronze_arrow", 120);
        var loadout = Catalog.Resolve(State(Equipped("main_hand", "weapon.training_shortbow", 1), arrows));

        Assert.Equal(CombatLoadoutStatus.Ready, loadout.Status);
        var attack = loadout.Attack!;
        Assert.Equal("ranged", attack.CombatStyle);
        Assert.Equal(10, attack.MinimumDamage);
        Assert.Equal(18, attack.MaximumDamage);
        Assert.Equal(72u, attack.IntervalTicks);
        Assert.Equal(10f, attack.Range);
        Assert.Equal(30f, attack.ProjectileSpeed);
        Assert.Equal("arrow", attack.AmmunitionFamily);
        Assert.Equal(arrows.ItemInstanceId, loadout.Ammunition!.ItemInstanceId);
        Assert.Equal(5, loadout.Ammunition.DamageBonusPercent);
        Assert.Equal(120, loadout.Ammunition.Quantity);
    }

    [Fact]
    public void AMissingWeaponOrArrowsReportsWhy()
    {
        Assert.Equal(CombatLoadoutStatus.WeaponMissing, Catalog.Resolve(State()).Status);
        Assert.Equal(
            CombatLoadoutStatus.WeaponMissing,
            Catalog.Resolve(State(Equipped("main_hand", "tool.starter_pickaxe", 1))).Status);
        Assert.Equal(
            CombatLoadoutStatus.AmmunitionMissing,
            Catalog.Resolve(State(Equipped("main_hand", "weapon.training_shortbow", 1))).Status);
    }

    [Fact]
    public void ArrowsOfAnotherFamilyDoNotFit()
    {
        var catalog = ItemCatalogTestData.Compile();
        catalog.AmmunitionFamilies = [.. catalog.AmmunitionFamilies, new ItemCatalogIdentityEntry { Id = "bolt", DisplayName = "Bolt" }];
        catalog.Definitions =
        [
            .. catalog.Definitions,
            new ItemDefinition
            {
                Id = "ammunition.test_bolt",
                Category = ItemCategoryIds.Ammunition,
                Ammunition = new ItemAmmunitionEntry { AmmunitionFamily = "bolt", DamageBonusPercent = 0 }
            }
        ];

        var loadout = new CombatItemCatalog(catalog, 30).Resolve(State(
            Equipped("main_hand", "weapon.training_shortbow", 1),
            Equipped("ammunition", "ammunition.test_bolt", 10)));

        Assert.Equal(CombatLoadoutStatus.AmmunitionIncompatible, loadout.Status);
    }

    private static CharacterItemState State(params SimulationEquippedItem[] equipment)
    {
        return CharacterItemState.Create(new PlayerCarryState(1, 0, 200), equipment);
    }

    private static SimulationEquippedItem Equipped(string slot, string definitionId, int quantity)
    {
        return new SimulationEquippedItem(slot, Guid.NewGuid(), definitionId, quantity, 1);
    }
}

public sealed class CombatGeometryTests
{
    [Fact]
    public void RangeIsMeasuredToTheClosestPointOfTheCapsule()
    {
        var bounds = CombatBounds.Capsule(new SimulationVector3(10f, 0f, 0f), 0.35f, 2f);

        Assert.Equal(9.65f, bounds.DistanceFrom(new SimulationVector3(0f, 1f, 0f)), 3);
        Assert.Equal(0f, bounds.DistanceFrom(new SimulationVector3(10f, 1f, 0.2f)));
        Assert.Equal(new SimulationVector3(10f, 1f, 0f), bounds.Center);
    }

    [Fact]
    public void RangeIsMeasuredToTheClosestPointOfTheBox()
    {
        var bounds = CombatBounds.Box(
            new SimulationVector3(5f, 0f, 0f),
            new SimulationVector3(5f, 0.5f, 0f),
            new SimulationVector3(2f, 1f, 4f));

        Assert.Equal(4f, bounds.DistanceFrom(new SimulationVector3(0f, 0.5f, 0f)), 3);
        Assert.Equal(0f, bounds.DistanceFrom(new SimulationVector3(5f, 0.5f, 1f)));
    }

    [Fact]
    public void LineOfSightUsesTheCombatQueryLayerOnly()
    {
        var lineOfSight = new CombatLineOfSight(CollisionTestWorldFactory.Create());

        // The test ground only blocks movement, so it never blocks an arrow.
        Assert.True(lineOfSight.HasLineOfSight(
            CombatLineOfSight.EyeOf(new SimulationVector3(0f, 0f, 0f)),
            new SimulationVector3(10f, -1f, 0f)));
    }
}

public sealed class MobLootRollerTests
{
    [Fact]
    public void EveryEntryRollsItsChanceAndQuantityInOrder()
    {
        var profile = new WorldActorLootProfileDefinition
        {
            Id = "mob.test",
            Entries =
            [
                new WorldActorLootEntryDefinition { DefinitionId = "material.wolf_pelt", MinimumQuantity = 1, MaximumQuantity = 1, ChanceBasisPoints = 10_000 },
                new WorldActorLootEntryDefinition { DefinitionId = "ammunition.bronze_arrow", MinimumQuantity = 3, MaximumQuantity = 8, ChanceBasisPoints = 4_000 }
            ]
        };

        // A sure entry rolls no chance; the 40 percent entry hits on a roll below 4000.
        var hit = MobLootRoller.Roll(profile, new SequenceRoll(1, 3999, 6));
        var miss = MobLootRoller.Roll(profile, new SequenceRoll(1, 4000));

        Assert.Equal(
            new[] { ("material.wolf_pelt", 1), ("ammunition.bronze_arrow", 6) },
            hit.Select(seed => (seed.DefinitionId, seed.Quantity)));
        Assert.Equal(new[] { ("material.wolf_pelt", 1) }, miss.Select(seed => (seed.DefinitionId, seed.Quantity)));
        Assert.Empty(MobLootRoller.Roll(null, new SequenceRoll()));
    }

    private sealed class SequenceRoll(params int[] values) : ICombatRandom
    {
        private int index;

        public int NextInclusive(int minimum, int maximum)
        {
            var value = values[index++];
            Assert.InRange(value, minimum, maximum);
            return value;
        }
    }
}
