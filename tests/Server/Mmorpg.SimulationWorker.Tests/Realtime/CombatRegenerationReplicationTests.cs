using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Health regeneration travels as a rule: the state a viewer holds names the regeneration, every
// viewer computes the health with RealtimeCombatHealth, and a regeneration step reaches only the
// owner. The real combat system runs tick by tick, so the computed health is compared with the
// worker's at every tick.
public sealed class CombatRegenerationReplicationTests
{
    private const int Damage = 15;
    private const int Regeneration = 7;
    private const string Sword = "test.sword";
    private static readonly Guid SwordItemId = Guid.Parse("5e5e5e5e-0000-4000-8000-000000000004");

    [Fact]
    public void ViewersComputeTheWorkersHealthAtEveryTickWithoutAStatePerStep()
    {
        using var fixture = new CombatTickFixture(
            RealtimeCombatTests.TestRules(combatTimeoutSeconds: 1f, healthRegenerationPerSecond: Regeneration),
            SwordCatalog());
        var tickRate = fixture.TickRateHz;
        var attacker = fixture.Join(1, "Regeneration Attacker", new SimulationEquippedItem("main_hand", SwordItemId, Sword, 1, 1));
        var target = fixture.Join(2, "Regeneration Target");
        var viewer = fixture.Join(3, "Regeneration Viewer");
        fixture.ShowEveryoneToEveryone();
        var watchers = new List<CombatTickPlayer> { viewer };
        Check();

        // The fight: three cuts take the target from 100 to 55.
        Attack();
        RunUntil(() => Health() <= 100 - (3 * Damage), 4 * tickRate, "three cuts");
        Clear();
        Assert.Equal(55, Health());

        // The leave of combat names the regeneration: 7 health a second from one second on.
        RunUntil(() => !fixture.CombatantOf(target).InCombat, 3 * tickRate, "the target to leave combat");
        var leftAt = fixture.ServerTick;
        var statesAtLeave = viewer.StatesOf(target.EntityId).Count;
        var leave = viewer.StatesOf(target.EntityId)[^1];
        Assert.Equal(
            (55, false, Regeneration, leftAt + (uint)tickRate),
            (leave.Health, leave.InCombat, leave.HealthRegenerationAmount, leave.HealthRegenerationTick));

        // Two steps reach the owner only.
        Run(2 * tickRate);
        Assert.Equal(69, Health());
        Assert.Equal(statesAtLeave, viewer.StatesOf(target.EntityId).Count);

        // A cut in the middle of the regeneration is a new state at once, without regeneration.
        Attack();
        RunUntil(() => Health() < 69, tickRate, "the cut during the regeneration");
        Clear();
        var hit = viewer.StatesOf(target.EntityId)[^1];
        Assert.Equal(
            (69 - Damage, true, 0, 0u),
            (hit.Health, hit.InCombat, hit.HealthRegenerationAmount, hit.HealthRegenerationTick));

        // The regeneration from low health to full sends the viewers nothing but the leave of
        // combat, also to a viewer whose spawn of the target came in the middle of it.
        RunUntil(() => !fixture.CombatantOf(target).InCombat, 3 * tickRate, "the target to leave combat again");
        var leftAgainAt = fixture.ServerTick;
        var statesAtSecondLeave = viewer.StatesOf(target.EntityId).Count;
        Run(2 * tickRate);
        var late = fixture.Join(4, "Late Viewer");
        fixture.ShowWorldTo(late);
        watchers.Add(late);
        Check();
        RunUntil(() => Health() == 100, 8 * tickRate, "full health");
        Run(2 * tickRate);

        Assert.Equal(statesAtSecondLeave, viewer.StatesOf(target.EntityId).Count);
        Assert.Single(late.StatesOf(target.EntityId));
        Assert.Equal(
            [61, 68, 75, 82, 89, 96, 100],
            target.OwnerStates().Where(state => state.ServerTick > leftAgainAt).Select(state => state.Health));
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);

        int Health() => fixture.CombatantOf(target).Health;

        void Attack() => fixture.SendCombatIntent(
            attacker,
            RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), target.EntityId));

        void Clear() => fixture.SendCombatIntent(attacker, RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));

        void Run(int ticks)
        {
            for (var tick = 0; tick < ticks; tick++)
            {
                fixture.RunTick();
                Check();
            }
        }

        void RunUntil(Func<bool> condition, int maximumTicks, string description)
        {
            for (var tick = 0; tick < maximumTicks && !condition(); tick++)
            {
                fixture.RunTick();
                Check();
            }

            Assert.True(condition(), $"Waited {maximumTicks} ticks for {description}.");
        }

        // Every watcher's last state of the target gives the worker's health at this tick.
        void Check()
        {
            var health = Health();
            foreach (var watcher in watchers)
            {
                var computed = RealtimeCombatHealth.At(watcher.StatesOf(target.EntityId)[^1], fixture.ServerTick, tickRate);
                Assert.True(
                    computed == health,
                    $"At tick {fixture.ServerTick} {watcher.Context.Session!.CharacterName} computes {computed}, the worker has {health}.");
            }
        }
    }

    // Vitals restored at a join start a regeneration whose first step is due at once: a spawn
    // between ticks names the current tick, one tick before the step, and the step on the next
    // tick, after the tick its state named, starts the rhythm of later steps as a new state.
    [Fact]
    public void AStepAfterTheTickItsStateNamedIsANewStateForTheViewers()
    {
        using var fixture = new CombatTickFixture(
            RealtimeCombatTests.TestRules(combatTimeoutSeconds: 1f, healthRegenerationPerSecond: Regeneration),
            SwordCatalog());
        var tickRate = fixture.TickRateHz;
        var target = fixture.Join(1, "Restored Target");
        var viewer = fixture.Join(2, "Restored Viewer");
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(target), 40, 100));
        fixture.ShowEveryoneToEveryone();

        var spawned = Assert.Single(viewer.StatesOf(target.EntityId));
        Assert.Equal(
            (40, Regeneration, fixture.ServerTick),
            (spawned.Health, spawned.HealthRegenerationAmount, spawned.HealthRegenerationTick));
        Assert.Equal(47, RealtimeCombatHealth.At(spawned, fixture.ServerTick, tickRate));

        fixture.RunTick();
        Assert.Equal(47, fixture.CombatantOf(target).Health);
        var rhythm = viewer.StatesOf(target.EntityId)[^1];
        Assert.Equal(
            (47, Regeneration, fixture.ServerTick + (uint)tickRate),
            (rhythm.Health, rhythm.HealthRegenerationAmount, rhythm.HealthRegenerationTick));

        for (var tick = 0; tick < 9 * tickRate; tick++)
        {
            fixture.RunTick();
            var health = fixture.CombatantOf(target).Health;
            Assert.Equal(health, RealtimeCombatHealth.At(viewer.StatesOf(target.EntityId)[^1], fixture.ServerTick, tickRate));
        }

        Assert.Equal(100, fixture.CombatantOf(target).Health);
        Assert.Equal(2, viewer.StatesOf(target.EntityId).Count);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // A combat phase that failed leaves the next step overdue. The late step starts another
    // rhythm than the viewers' state names, so it reaches them as a new state.
    [Fact]
    public void AnOverdueStepIsANewStateForTheViewers()
    {
        using var fixture = new CombatTickFixture(
            RealtimeCombatTests.TestRules(combatTimeoutSeconds: 1f, healthRegenerationPerSecond: Regeneration),
            SwordCatalog());
        var tickRate = fixture.TickRateHz;
        var target = fixture.Join(1, "Overdue Target");
        var viewer = fixture.Join(2, "Overdue Viewer");
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(target), 40, 100));
        fixture.ShowEveryoneToEveryone();
        fixture.RunTick();
        fixture.RunTick();
        var statesBefore = viewer.StatesOf(target.EntityId).Count;

        fixture.CombatantOf(target).NextRegenerationTick = fixture.ServerTick;
        fixture.RunTick();

        Assert.Equal(54, fixture.CombatantOf(target).Health);
        Assert.Equal(statesBefore + 1, viewer.StatesOf(target.EntityId).Count);
        for (var tick = 0; tick < 8 * tickRate; tick++)
        {
            fixture.RunTick();
            var health = fixture.CombatantOf(target).Health;
            Assert.Equal(health, RealtimeCombatHealth.At(viewer.StatesOf(target.EntityId)[^1], fixture.ServerTick, tickRate));
        }

        Assert.Equal(100, fixture.CombatantOf(target).Health);
        Assert.Equal(statesBefore + 1, viewer.StatesOf(target.EntityId).Count);
    }

    private static ItemCatalogRuntimeDocument SwordCatalog()
    {
        return new ItemCatalogRuntimeDocument
        {
            WeaponFamilies =
            [
                new ItemWeaponFamilyEntry
                {
                    Id = "sword",
                    CombatStyle = CombatStyleIds.Melee,
                    AmmunitionFamily = string.Empty,
                    ProjectileSpeed = 0f,
                    WindupSeconds = 0f
                }
            ],
            Definitions =
            [
                new ItemDefinition
                {
                    Id = Sword,
                    Category = ItemCategoryIds.Weapon,
                    Weapon = new ItemWeaponEntry
                    {
                        WeaponFamily = "sword",
                        DamageType = DamageTypeIds.Normal,
                        MinimumDamage = Damage,
                        MaximumDamage = Damage,
                        AttackIntervalSeconds = 1f,
                        Range = 3f,
                        ManaCost = 0
                    }
                }
            ]
        };
    }
}
