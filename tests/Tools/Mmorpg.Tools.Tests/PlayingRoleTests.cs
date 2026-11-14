using Mmorpg.GameProtocol;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.StackStressGenerator;
using static Mmorpg.Tools.Tests.PlayingTestWorld;

namespace Mmorpg.Tools.Tests;

// The roles of the playing workload as state machines on a fake body and a fake clock.
public sealed class PlayingRoleTests
{
    private const ulong Wolf = 100;
    private static readonly PlayingGround Field =
        new("west-field", PlayingGroundKind.Hunting, new PlayingRectangle(-150, -50, -120, -20));

    [Fact]
    public void AFighterEquipsTeleportsHuntsAMobAndLootsItsCorpse()
    {
        var test = new FighterTest(teleport: true);

        test.Think();
        Assert.Equal([PlayingOperations.Equip], test.Body.Sent);
        test.Body.Answer();
        test.Body.View.Items.Load(Inventory(4, Worn("weapon.iron_sword", "main_hand")));
        test.Think();
        test.Think();
        Assert.Equal(PlayingOperations.Teleport, test.Body.Pending);
        Assert.Equal(-135f, test.Body.Teleported!.Value.X);
        Assert.Equal(-35f, test.Body.Teleported!.Value.Z);
        test.Body.Answer();
        test.Body.MoveTo(-135f, -35f);

        test.AddWolf(-130f, -35f);
        test.Think();
        Assert.Equal("approach", test.Role.State);
        Assert.NotEmpty(test.Body.Walks);
        test.Body.MoveTo(-131.5f, -35f);
        test.Think();
        Assert.Equal(PlayingOperations.Attack, test.Body.Pending);
        Assert.Equal(Wolf, test.Body.AttackedTarget);
        test.Body.Answer();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(2, target: Wolf, status: RealtimeCombatAttackStatus.Attacking));
        test.Think();
        Assert.Equal("fight", test.Role.State);

        test.KillWolf();
        test.Think();
        Assert.Equal("loot", test.Role.State);
        var corpse = Guid.NewGuid();
        test.Body.View.ApplyCorpsePresence(Corpses(1, MobCorpse(corpse, -130f, -35f)));
        test.Think();
        Assert.Equal(PlayingOperations.LootOpen, test.Body.Pending);
        Assert.Equal(corpse, test.Body.OpenedCorpse);
        test.Body.Answer();
        test.Body.View.ApplyCorpseInteractionResult(CorpseResult(corpse, RealtimeCorpseInteractionKind.Open));
        test.Body.View.ApplyCorpseViewState(View(corpse, 5));
        test.Think();
        Assert.Equal(PlayingOperations.LootAll, test.Body.Pending);
        test.Body.Answer();
        test.Think();
        Assert.Equal(PlayingOperations.LootClose, test.Body.Pending);
        test.Body.Answer();
        test.Think();

        Assert.Equal(
            [
                PlayingOperations.Equip,
                PlayingOperations.Teleport,
                PlayingOperations.Attack,
                PlayingOperations.LootOpen,
                PlayingOperations.LootAll,
                PlayingOperations.LootClose
            ],
            test.Body.Sent);
        Assert.Equal("seek", test.Role.State);
    }

    // A rejection the bot can explain still pauses it, so no bot retries in a tight loop.
    [Fact]
    public void ARejectedAttackIsFollowedByAPauseAndThenAnotherChoice()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131.5f, -35f);
        test.AddWolf(-130f, -35f);
        test.Think();
        Assert.Equal(PlayingOperations.Attack, test.Body.Pending);

        test.Body.Answer(succeeded: false, code: "combat_target_dead");
        test.Body.View.ApplyCombatantState(Combatant(Wolf, 2, 0, RealtimeCombatLifeState.Dead));
        test.AddWolf(-130f, -35.5f, entityId: Wolf + 1);
        for (var think = 0; think < 4; think++)
        {
            test.Think(TimeSpan.FromMilliseconds(400));
        }

        Assert.Equal([PlayingOperations.Attack], test.Body.Sent);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.RejectionPauses]);
        test.Think(PlayingRoleBase.MaximumRejectionPause);
        test.Think();
        Assert.Equal(PlayingOperations.Attack, test.Body.Pending);
        Assert.Equal(Wolf + 1, test.Body.AttackedTarget);
    }

    // Every expected conflict of the fighter's operations: the body reports it as expected, the
    // fighter pauses without another intent, and then it plays on.
    [Theory]
    [InlineData(PlayingOperations.Attack, "combat_target_dead")]
    [InlineData(PlayingOperations.Attack, "combat_target_not_found")]
    [InlineData(PlayingOperations.Attack, "combat_target_not_attackable")]
    [InlineData(PlayingOperations.Attack, "combat_character_dead")]
    [InlineData(PlayingOperations.LootOpen, "corpse_not_found")]
    [InlineData(PlayingOperations.LootOpen, "combat_character_dead")]
    [InlineData(PlayingOperations.LootAll, "corpse_not_found")]
    [InlineData(PlayingOperations.LootAll, "inventory_full")]
    [InlineData(PlayingOperations.LootAll, "combat_character_dead")]
    public void EveryExpectedConflictOfAFighterPausesItAndItPlaysOn(string operation, string code)
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131.5f, -35f);
        test.AddWolf(-130f, -35f);
        test.Think();
        if (operation != PlayingOperations.Attack)
        {
            test.Body.Answer();
            test.Body.View.ApplyOwnerCombatState(OwnCombat(2, target: Wolf, status: RealtimeCombatAttackStatus.Attacking));
            test.Think();
            test.KillWolf();
            test.Think();
            var corpse = Guid.NewGuid();
            test.Body.View.ApplyCorpsePresence(Corpses(1, MobCorpse(corpse, -130f, -35f)));
            test.Think();
            if (operation == PlayingOperations.LootAll)
            {
                test.Body.Answer();
                test.Body.View.ApplyCorpseInteractionResult(CorpseResult(corpse, RealtimeCorpseInteractionKind.Open));
                test.Body.View.ApplyCorpseViewState(View(corpse, 5));
                test.Think();
            }
        }

        Assert.Equal(operation, test.Body.Pending);
        test.Body.Answer(succeeded: false, code: code);
        Assert.True(test.Body.Outcome!.Expected);
        var sent = test.Body.Sent.Count;
        test.Think(TimeSpan.FromMilliseconds(500));
        test.Think(TimeSpan.FromMilliseconds(500));
        test.Think(TimeSpan.FromMilliseconds(500));

        Assert.Equal(sent, test.Body.Sent.Count);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.RejectionPauses]);
        test.AddWolf(-130f, -35.5f, entityId: Wolf + 1);
        test.Think(PlayingRoleBase.MaximumRejectionPause);
        test.Think();
        test.Think();
        Assert.True(test.Body.Sent.Count > sent);
    }

    [Fact]
    public void AFighterLeavesAMobThatAnotherPlayersAttackOwns()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131.5f, -35f);
        test.Body.View.ApplyWorldActorSpawn(Mob(Wolf, -130f, -35f));
        test.Body.View.ApplyCombatantState(Combatant(Wolf, 1, taggedBy: 999));

        test.Think();

        Assert.Empty(test.Body.Sent);
        Assert.Equal("seek", test.Role.State);
    }

    // In range behind cover the worker reports a blocked line of sight. Walking into range
    // again would stand still, so the fighter waits a moment and then takes another Mob.
    [Fact]
    public void AFighterInRangeBehindCoverGivesTheTargetUpAfterAMoment()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131.5f, -35f);
        test.AddWolf(-130f, -35f);
        test.Think();
        test.Body.Answer();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(2, target: Wolf, status: RealtimeCombatAttackStatus.LineOfSightBlocked));

        test.Think();
        Assert.Equal("fight", test.Role.State);
        Assert.Equal([PlayingOperations.Attack], test.Body.Sent);
        Assert.Empty(test.Body.Walks);

        test.Think(FighterRole.LineOfSightPatience);
        Assert.Equal(PlayingOperations.ClearTarget, test.Body.Pending);
        test.Body.Answer();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(3));
        test.Think();

        // The wolf is ignored for a while, so the fighter does not choose it again at once.
        Assert.Equal("seek", test.Role.State);
        Assert.Equal([PlayingOperations.Attack, PlayingOperations.ClearTarget], test.Body.Sent);
    }

    [Fact]
    public void AFighterDoesNotLootACorpseThatAnotherPlayerOwnsAndCountsIt()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131.5f, -35f);
        test.AddWolf(-130f, -35f);
        test.Think();
        test.Body.Answer();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(2, target: Wolf, status: RealtimeCombatAttackStatus.Attacking));
        test.Think();
        test.KillWolf();
        test.Think();

        test.Body.View.ApplyCorpsePresence(Corpses(1, MobCorpse(Guid.NewGuid(), -130f, -35f, lootable: false)));
        test.Think();

        Assert.Equal([PlayingOperations.Attack], test.Body.Sent);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.LootRefusedOwned]);
        Assert.Equal("seek", test.Role.State);
    }

    [Fact]
    public void AFighterWithLowHealthStopsAttackingWalksAwayAndRests()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131.5f, -35f);
        test.AddWolf(-130f, -35f);
        test.Think();
        test.Body.Answer();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(2, target: Wolf, status: RealtimeCombatAttackStatus.Attacking));
        test.Think();

        test.Body.View.ApplyOwnerCombatState(OwnCombat(3, health: 20, target: Wolf, status: RealtimeCombatAttackStatus.Attacking));
        test.Think();
        Assert.Equal(PlayingOperations.ClearTarget, test.Body.Pending);
        test.Body.Answer();
        test.Think();
        Assert.Equal("flee", test.Role.State);
        var away = test.Body.Walks[^1];
        Assert.True(away.X < -131.5f);

        test.Body.MoveState = PlayingMoveState.Arrived;
        test.Body.MoveTo(away.X, away.Z);
        test.Think();
        Assert.Equal("rest", test.Role.State);
    }

    // After a death the fighter waits for the respawn, walks back to its own corpse, and loots
    // it back.
    [Fact]
    public void AFighterRecoversItsOwnCorpseAfterADeath()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131f, -36f);
        test.Think();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(5, health: 0, lifeState: RealtimeCombatLifeState.Dead));
        test.Think();
        Assert.Equal("dead", test.Role.State);

        test.Body.View.ApplyOwnerCombatState(OwnCombat(6, lifeSequence: 2));
        test.Body.MoveTo(0f, -12f);
        test.Body.View.Items.Load(Inventory(9));
        test.Think();
        Assert.Equal("recover", test.Role.State);
        test.Think();
        Assert.Equal(-131f, test.Body.Walks[^1].X);
        Assert.Equal(-36f, test.Body.Walks[^1].Z);

        var corpse = Guid.NewGuid();
        test.Body.MoveState = PlayingMoveState.Arrived;
        test.Body.MoveTo(-130.5f, -36f);
        test.Body.View.ApplyCorpsePresence(Corpses(1, PlayerCorpse(corpse, -131f, -36f)));
        test.Think();
        Assert.Equal(PlayingOperations.LootOpen, test.Body.Pending);
        test.Body.Answer();
        test.Body.View.ApplyCorpseInteractionResult(CorpseResult(corpse, RealtimeCorpseInteractionKind.Open));
        test.Body.View.ApplyCorpseViewState(View(corpse, 3));
        test.Think();
        Assert.Equal(PlayingOperations.LootAll, test.Body.Pending);
        test.Body.Answer();
        test.Body.View.Items.Load(Inventory(10, Worn("weapon.iron_sword", "main_hand")));
        test.Think();
        Assert.Equal(PlayingOperations.LootClose, test.Body.Pending);
        test.Body.Answer();
        test.Think();

        Assert.Equal(1, test.Body.Counters[PlayingCounter.CorpsesRecovered]);
        Assert.Equal("seek", test.Role.State);
        Assert.False(test.Role.ContinuesAsWalker);
    }

    [Fact]
    public void AFighterWhoseCorpseIsGoneGoesOnAsAWalkerAndTheReportCountsIt()
    {
        var test = new FighterTest(teleport: false, equipped: true);
        test.Body.MoveTo(-131f, -36f);
        test.Think();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(5, health: 0, lifeState: RealtimeCombatLifeState.Dead));
        test.Think();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(6, lifeSequence: 2));
        test.Body.View.Items.Load(Inventory(9));
        test.Think();

        test.Body.MoveTo(-130f, -36f);
        test.Body.View.ApplyCorpsePresence(Corpses(1));
        for (var think = 0; think < 20; think++)
        {
            test.Think(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(1, test.Body.Counters[PlayingCounter.CorpsesLost]);
        Assert.True(test.Role.ContinuesAsWalker);
        Assert.Equal("walker", test.Role.State);
    }

    [Fact]
    public void AWalkerTeleportsHomeOnceAndThenWalksAndPausesTheSameWayForTheSameSeed()
    {
        var home = new PlayingGround("north-road", PlayingGroundKind.Walking, new PlayingRectangle(-10, 44, 10, 60));
        var first = WalkerDestinations(home, seed: 11);
        var again = WalkerDestinations(home, seed: 11);
        var other = WalkerDestinations(home, seed: 12);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEmpty(first);
    }

    [Fact]
    public void TheRosterFollowsTheSharesAndGivesTheSameSeedTheSameAssignments()
    {
        var scenario = new PlayingScenario(
            new Dictionary<PlayingRole, int> { [PlayingRole.Fighter] = 60, [PlayingRole.Walker] = 40 },
            25,
            true,
            [
                Field,
                new PlayingGround("south-field", PlayingGroundKind.Hunting, new PlayingRectangle(-30, -160, 0, -130)),
                new PlayingGround("north-road", PlayingGroundKind.Walking, new PlayingRectangle(-10, 44, 10, 60))
            ]);

        var assignments = PlayingRoster.Assign(scenario, 20, 1, 1337);

        Assert.Equal(20, assignments.Count);
        Assert.Equal(12, assignments.Count(assignment => assignment.Role == PlayingRole.Fighter));
        Assert.Equal(8, assignments.Count(assignment => assignment.Role == PlayingRole.Walker));
        Assert.Equal(3, assignments.Count(assignment => assignment.Kit == PlayingRoster.BowKit));
        Assert.Equal(9, assignments.Count(assignment => assignment.Kit == PlayingRoster.SwordKit));
        Assert.Equal(6, assignments.Count(assignment => assignment.Home?.Name == "west-field"));
        Assert.Equal(6, assignments.Count(assignment => assignment.Home?.Name == "south-field"));
        Assert.All(
            assignments.Where(assignment => assignment.Role == PlayingRole.Walker),
            assignment => Assert.Equal("north-road", assignment.Home?.Name));
        Assert.Equal(assignments, PlayingRoster.Assign(scenario, 20, 1, 1337));
        Assert.NotEqual(
            assignments.Select(assignment => assignment.Role),
            PlayingRoster.Assign(scenario, 20, 1, 7).Select(assignment => assignment.Role));
    }

    // The worker refuses item operations, combat intents, corpse interactions, and every world
    // interaction but a close from a dead character, so a death that races any of them is an
    // expected conflict, not a failed run.
    [Theory]
    [InlineData(PlayingOperations.Equip)]
    [InlineData(PlayingOperations.ItemMove)]
    [InlineData(PlayingOperations.Attack)]
    [InlineData(PlayingOperations.ClearTarget)]
    [InlineData(PlayingOperations.LootOpen)]
    [InlineData(PlayingOperations.LootAll)]
    [InlineData(PlayingOperations.LootClose)]
    [InlineData(PlayingOperations.GatherOpen)]
    [InlineData(PlayingOperations.GatherStart)]
    [InlineData(PlayingOperations.VendorOpen)]
    [InlineData(PlayingOperations.VendorSale)]
    [InlineData(PlayingOperations.VendorPurchase)]
    public void ADeathThatRacesAnIntentIsAnExpectedConflict(string operation)
    {
        Assert.True(PlayingExpectedConflicts.IsExpected(operation, "combat_character_dead"));
    }

    [Fact]
    public void EveryExpectedConflictHasAReasonAndBelongsToAKnownOperation()
    {
        Assert.Equal(
            PlayingOperations.All.Order(StringComparer.Ordinal),
            PlayingExpectedConflicts.Table.Keys.Order(StringComparer.Ordinal));
        Assert.All(
            PlayingExpectedConflicts.Table.SelectMany(operation => operation.Value),
            conflict =>
            {
                Assert.Matches("^[a-z]+(_[a-z]+)+$", conflict.Key);
                Assert.True(conflict.Value.Length > 20, conflict.Key);
            });
        Assert.False(PlayingExpectedConflicts.IsExpected(PlayingOperations.Teleport, "admin_tools_disabled"));
        Assert.True(PlayingExpectedConflicts.IsExpected(PlayingOperations.Attack, "combat_target_dead"));
        Assert.False(PlayingExpectedConflicts.IsExpected(PlayingOperations.Attack, "combat_intent_rate_limited"));
    }

    private static List<(float X, float Z)> WalkerDestinations(PlayingGround home, int seed)
    {
        var body = new FakePlayingBody(PlayingKit.None);
        body.View.Items.Load(Inventory(3));
        var walker = new WalkerRole(home, teleport: true, new Random(seed));
        var now = TimeSpan.Zero;
        walker.Think(body, now);
        Assert.Empty(body.Sent);
        walker.Think(body, now);
        Assert.Equal([PlayingOperations.Teleport], body.Sent);
        Assert.Equal(0f, body.Teleported!.Value.X);
        Assert.Equal(52f, body.Teleported!.Value.Z);
        body.Answer();
        body.MoveTo(0f, 52f);
        for (var think = 0; think < 120; think++)
        {
            now += TimeSpan.FromMilliseconds(500);
            walker.Think(body, now);
            if (think % 9 == 8)
            {
                body.MoveState = PlayingMoveState.Arrived;
            }
        }

        Assert.Equal([PlayingOperations.Teleport], body.Sent);
        return body.Walks.Select(walk => (walk.X, walk.Z)).ToList();
    }

    private sealed class FighterTest
    {
        private TimeSpan now;

        public FighterTest(bool teleport, bool equipped = false)
        {
            Body = new FakePlayingBody(SwordKit);
            Body.View.Items.Load(equipped
                ? Inventory(4, Worn("weapon.iron_sword", "main_hand"))
                : Inventory(3, Carried("weapon.iron_sword", 0)));
            Body.View.ApplyOwnerCombatState(OwnCombat(1));
            Role = new FighterRole(Field, teleport, new Random(5));
            if (equipped)
            {
                // The first think finds the kit worn.
                Think();
            }
        }

        public FakePlayingBody Body { get; }

        public FighterRole Role { get; }

        public void Think(TimeSpan? step = null)
        {
            now += step ?? TimeSpan.FromMilliseconds(400);
            Role.Think(Body, now);
            Body.Events.Clear();
        }

        public void AddWolf(float x, float z, ulong entityId = Wolf)
        {
            Body.View.ApplyWorldActorSpawn(Mob(entityId, x, z));
            Body.View.ApplyCombatantState(Combatant(entityId, 1));
        }

        // The wolf dies to the fighter: the Died event, its dead state, and its despawn.
        public void KillWolf()
        {
            Body.Events.Add(new RealtimeCombatEvent(
                RealtimeCombatEventKind.Died,
                OwnEntity,
                Wolf,
                10,
                0,
                RealtimeCombatDamageType.Normal,
                0,
                RealtimeCombatAttackStyle.Melee));
            Body.View.ApplyCombatantState(Combatant(Wolf, 9, 0, RealtimeCombatLifeState.Dead, taggedBy: OwnEntity));
            Body.View.ApplyWorldActorDespawn(new RealtimeWorldActorDespawn(Wolf, Guid.NewGuid(), "died"));
        }
    }
}
