using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.Tools.StackStressGenerator;
using static Mmorpg.Tools.Tests.PlayingTestWorld;

namespace Mmorpg.Tools.Tests;

// The vendor trip of the role base, the fighter's caution and arrows, the gatherer, and the
// trader, as state machines on a fake body and a fake clock.
public sealed class PlayingTradeAndGatherTests
{
    private const ulong VendorEntity = 500;
    private const ulong FirstNode = 600;
    private static readonly PlayingGround Field =
        new("west-field", PlayingGroundKind.Hunting, new PlayingRectangle(-150, -50, -120, -20));

    private static readonly PlayingGround Veins =
        new("meadow-veins", PlayingGroundKind.Nodes, new PlayingRectangle(50, -48, 70, -32), "pickaxe");

    private static readonly PlayingGround Town =
        new("town", PlayingGroundKind.Town, new PlayingRectangle(-24, -24, 24, 4));

    [Fact]
    public void AFighterThatRunsOutOfRoomSellsItsLootAtTheVendorAndWalksHome()
    {
        var test = new RoleTest(SwordKit, body => new FighterRole(Field, teleport: false, new Random(5), Town));
        var loaded = Inventory(4, Worn("weapon.iron_sword", "main_hand"), Carried(Pelt, 0, 10), Carried(Ore, 1, 5));
        test.Body.View.Items.Load(loaded with { CarriedWeight = 1900 });
        test.Body.MoveTo(-135f, -35f);

        // The first think finds the kit worn.
        test.Think();
        test.Think();
        Assert.Equal(1, test.Body.Counters[PlayingCounter.VendorTrips]);
        test.Think();
        // The vendor is out of interest: the fighter walks toward the town first.
        Assert.Equal(-10f, test.Body.Walks[^1].Z);
        test.ShowVendor();
        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.VendorOpen, test.Body.Pending);
        Assert.Equal(VendorEntity, test.Body.OpenedActor!.EntityId);
        test.Answer();

        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.VendorSale, test.Body.Pending);
        Assert.Equal(Pelt, test.Body.LastAction!.Item!.DefinitionId);
        Assert.Equal(10, test.Body.LastAction.Quantity);
        Assert.Equal(60, test.Body.LastAction.ExpectedPrice);
        test.Answer();
        test.Body.View.Items.Load(Inventory(5, Worn("weapon.iron_sword", "main_hand"), Carried(Ore, 1, 5)));
        test.DriveUntilPending();
        Assert.Equal(Ore, test.Body.LastAction!.Item!.DefinitionId);
        Assert.Equal(20, test.Body.LastAction.ExpectedPrice);
        test.Answer();
        test.Body.View.Items.Load(Inventory(6, Worn("weapon.iron_sword", "main_hand")));
        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.VendorClose, test.Body.Pending);
        test.Answer();

        test.DriveUntil(() => !((FighterRole)test.Role).OnVendorTrip);
        Assert.True(Field.Area.Contains(test.Body.Position.X, test.Body.Position.Z));
        Assert.Equal(
            [PlayingOperations.VendorOpen, PlayingOperations.VendorSale, PlayingOperations.VendorSale, PlayingOperations.VendorClose],
            test.Body.Sent);
        Assert.Equal(2, test.Body.Counters[PlayingCounter.VendorSales]);
        Assert.Equal(80, test.Body.Counters[PlayingCounter.CurrencyEarned]);
        Assert.False(test.Body.Counters.ContainsKey(PlayingCounter.VendorTripsWithoutRoom));
    }

    [Fact]
    public void ABowFighterLowOnArrowsBuysAsManyAsItCanPayFor()
    {
        var test = new RoleTest(BowKit, body => new FighterRole(Field, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(
            4,
            Worn("weapon.training_shortbow", "main_hand"),
            Worn(Arrow, "ammunition", 50)));
        test.Body.MoveTo(-135f, -35f);
        test.ShowVendor();

        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.VendorOpen, test.Body.Pending);
        test.Answer();
        test.DriveUntilPending();

        Assert.Equal(PlayingOperations.VendorPurchase, test.Body.Pending);
        Assert.Equal(Arrow, test.Body.LastAction!.OfferDefinitionId);
        Assert.Equal(100, test.Body.LastAction.Quantity);
        Assert.Equal(100, test.Body.LastAction.ExpectedPrice);
        test.Answer();
        var restocked = Inventory(
            5,
            Worn("weapon.training_shortbow", "main_hand"),
            Worn(Arrow, "ammunition", 50),
            Carried(Arrow, 0, 100));
        test.Body.View.Items.Load(restocked with { Currency = 0 });
        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.VendorClose, test.Body.Pending);
        Assert.Equal(100, test.Body.Counters[PlayingCounter.ArrowsBought]);
        Assert.Equal(100, test.Body.Counters[PlayingCounter.CurrencySpent]);
    }

    // Low on arrows and without currency or loot, the visit buys nothing. The fighter then
    // hunts with the arrows it has instead of walking to the vendor again and again.
    [Fact]
    public void ABowFighterThatCannotPayForArrowsHuntsOnWithWhatItHas()
    {
        var test = new RoleTest(BowKit, body => new FighterRole(Field, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(
            4,
            Worn("weapon.training_shortbow", "main_hand"),
            Worn(Arrow, "ammunition", 50)) with
        { Currency = 0 });
        test.Body.MoveTo(-135f, -35f);
        test.ShowVendor();
        test.AddMob(101, -131f, -35f, "mob.grey_wolf");

        test.DriveUntil(() => ((FighterRole)test.Role).Target == 101UL, answer: true);

        Assert.Equal(1, test.Body.Counters[PlayingCounter.VendorTrips]);
        Assert.DoesNotContain(PlayingOperations.VendorPurchase, test.Body.Sent);
    }

    [Fact]
    public void ABowFighterWithNeitherArrowsNorCurrencyGoesOnAsAWalker()
    {
        var test = new RoleTest(BowKit, body => new FighterRole(Field, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn("weapon.training_shortbow", "main_hand")) with { Currency = 0 });
        test.Body.MoveTo(-135f, -35f);
        test.ShowVendor();

        test.DriveUntil(() => ((FighterRole)test.Role).ContinuesAsWalker, answer: true);

        Assert.Equal(1, test.Body.Counters[PlayingCounter.VendorTrips]);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.ArrowsRanOut]);
        Assert.DoesNotContain(PlayingOperations.VendorPurchase, test.Body.Sent);
    }

    // A careful player pulls the Mob that no other aggressive Mob would join, even when it is
    // a little farther away.
    [Fact]
    public void AFighterPrefersAMobWithoutAggressiveNeighbors()
    {
        var test = new RoleTest(SwordKit, body => new FighterRole(Field, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn("weapon.iron_sword", "main_hand")));
        test.Body.MoveTo(-135f, -35f);
        test.AddMob(101, -131f, -35f, "mob.grey_wolf");
        test.AddMob(102, -127f, -35f, "mob.feral_wolf");
        test.AddMob(103, -126f, -32f, "mob.feral_wolf");
        test.AddMob(104, -140f, -45f, "mob.grey_wolf");

        test.Think();
        test.Think();

        Assert.Equal(104UL, ((FighterRole)test.Role).Target);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.CrowdedMobsSkipped]);
    }

    [Fact]
    public void AFighterFleesEarlierWhenTwoMobsAttackIt()
    {
        var test = new RoleTest(SwordKit, body => new FighterRole(Field, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn("weapon.iron_sword", "main_hand")));
        test.Body.MoveTo(-131.5f, -35f);
        test.AddMob(101, -130f, -35f, "mob.grey_wolf");
        test.Think();
        test.Think();
        Assert.Equal(PlayingOperations.Attack, test.Body.Pending);
        test.Answer();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(2, health: 55, target: 101, status: RealtimeCombatAttackStatus.Attacking));
        test.Body.View.ApplyCombatantState(Combatant(101, 2, target: OwnEntity, taggedBy: OwnEntity));
        test.AddMob(102, -129f, -36f, "mob.feral_wolf");
        test.Body.View.ApplyCombatantState(Combatant(102, 2, target: OwnEntity));

        test.Think();

        Assert.Equal(PlayingOperations.ClearTarget, test.Body.Pending);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.FighterFlees]);
    }

    [Fact]
    public void AGathererWorksTheNearestNodeItsToolFitsAndMovesOnWhenItDepletes()
    {
        var test = new RoleTest(PickaxeKit, body => new GathererRole(Veins, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn(Pickaxe, "tool")));
        test.Body.MoveTo(58f, -40f);
        var first = test.AddNode(FirstNode, 62f, -40f);
        test.AddNode(FirstNode + 1, 67f, -40f);

        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.GatherOpen, test.Body.Pending);
        Assert.Equal(FirstNode, test.Body.OpenedActor!.EntityId);
        var opened = Opened(first, RealtimeWorldActorCapabilityKind.Gathering, "resource.mine", "resource.iron_vein");
        test.Answer(opened);
        test.Think();
        Assert.Equal(PlayingOperations.GatherStart, test.Body.Pending);
        Assert.Equal(RealtimeCapabilityActionKind.StartGathering, test.Body.LastAction!.Action);
        test.Answer();
        test.Body.View.ApplyGatheringState(RealtimeGatheringState.CreateStarted(
            opened.InteractionSessionId, FirstNode, first.RuntimeActorId, 3000));
        test.Think();
        test.Think();
        Assert.Equal("gathering", test.Role.State);
        Assert.Null(test.Body.Pending);

        // The node depletes: the worker stops the gathering and closes the interaction.
        test.Body.View.ApplyGatheringState(RealtimeGatheringState.CreateStopped(
            opened.InteractionSessionId, FirstNode, first.RuntimeActorId, "resource_node_depleted", "depleted", []));
        test.Body.View.ApplyWorldActorState(ActorState(FirstNode, 2, 62f, -40f, active: false));
        test.Body.View.ApplyWorldInteractionClosed(Closed(opened, "world_actor_unavailable"));
        test.DriveUntilPending();

        Assert.Equal(PlayingOperations.GatherOpen, test.Body.Pending);
        Assert.Equal(FirstNode + 1, test.Body.OpenedActor!.EntityId);
        Assert.Equal(2, test.Body.Counters[PlayingCounter.GatherAttempts]);
        Assert.False(test.Body.Counters.ContainsKey(PlayingCounter.NodesFoundEmpty));
    }

    // The worker closes the interaction of a depleted node itself, so the gatherer waits for
    // that instead of sending a close the worker would refuse.
    [Fact]
    public void AGathererLetsTheWorkerCloseADepletedNode()
    {
        var test = new RoleTest(PickaxeKit, body => new GathererRole(Veins, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn(Pickaxe, "tool")));
        test.Body.MoveTo(58f, -40f);
        var node = test.AddNode(FirstNode, 62f, -40f);
        test.DriveUntilPending();
        var opened = Opened(node, RealtimeWorldActorCapabilityKind.Gathering, "resource.mine", "resource.iron_vein");
        test.Answer(opened);
        test.Think();
        test.Answer();
        test.Body.View.ApplyGatheringState(RealtimeGatheringState.CreateStarted(
            opened.InteractionSessionId, FirstNode, node.RuntimeActorId, 3000));
        test.Think();

        test.Body.View.ApplyGatheringState(RealtimeGatheringState.CreateStopped(
            opened.InteractionSessionId, FirstNode, node.RuntimeActorId, "resource_node_depleted", "depleted", []));
        test.Think();
        test.Think();
        Assert.Null(test.Body.Pending);
        test.Body.View.ApplyWorldInteractionClosed(Closed(opened, "world_actor_unavailable"));
        test.Think();

        Assert.Equal("seek", test.Role.State);
        Assert.DoesNotContain(PlayingOperations.GatherClose, test.Body.Sent);
    }

    [Fact]
    public void AGathererCountsANodeThatDepletedBeforeItArrivedAndWaitsWhileTheFieldIsEmpty()
    {
        var test = new RoleTest(PickaxeKit, body => new GathererRole(Veins, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn(Pickaxe, "tool")));
        test.Body.MoveTo(55f, -40f);
        test.AddNode(FirstNode, 66f, -40f);
        test.Think();
        test.Think();
        Assert.Equal("approach", test.Role.State);

        test.Body.View.ApplyWorldActorState(ActorState(FirstNode, 2, 66f, -40f, active: false));
        for (var think = 0; think < 5; think++)
        {
            test.Think();
        }

        Assert.Equal(1, test.Body.Counters[PlayingCounter.GatherAttempts]);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.NodesFoundEmpty]);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.NodeFieldsEmpty]);
        Assert.Empty(test.Body.Sent);
    }

    // Cover between every spot within reach and the node: the gatherer gives the node up for a
    // while instead of standing still in front of it.
    [Fact]
    public void AGathererGivesUpANodeBehindCover()
    {
        var test = new RoleTest(PickaxeKit, body => new GathererRole(Veins, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn(Pickaxe, "tool")));
        test.Body.MoveTo(58f, -40f);
        test.Body.SightBlocked = true;
        test.AddNode(FirstNode, 62f, -40f);

        for (var think = 0; think < 4; think++)
        {
            test.Think();
        }

        Assert.Empty(test.Body.Sent);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.NodesBlockedFromSight]);
        Assert.False(test.Body.Counters.ContainsKey(PlayingCounter.GatherAttempts));
    }

    [Fact]
    public void AGathererSellsItsYieldsWhenItsCarryCapacityRunsLowAndKeepsItsTool()
    {
        var test = new RoleTest(PickaxeKit, body => new GathererRole(Veins, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn(Pickaxe, "tool"), Carried(Ore, 0, 28)) with { CarriedWeight = 1830 });
        test.Body.MoveTo(58f, -40f);
        test.AddNode(FirstNode, 62f, -40f);
        test.ShowVendor();

        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.VendorOpen, test.Body.Pending);
        test.Answer();
        test.DriveUntilPending();

        Assert.Equal(PlayingOperations.VendorSale, test.Body.Pending);
        Assert.Equal(Ore, test.Body.LastAction!.Item!.DefinitionId);
        Assert.Equal(28, test.Body.LastAction.Quantity);
        Assert.Equal(112, test.Body.LastAction.ExpectedPrice);
        Assert.Equal(1, test.Body.Counters[PlayingCounter.VendorTrips]);
    }

    [Fact]
    public void ATraderVisitsTheVendorMakesAFewTradesKeepsItsReserveAndIdles()
    {
        var test = new RoleTest(PlayingKit.None, body => new TraderRole(Town, teleport: false, new Random(3)));
        test.Body.View.Items.Load(Inventory(4, Carried(Pelt, 0, 50)) with { Currency = 30 });
        test.Body.MoveTo(0f, -10f);
        test.ShowVendor();

        test.DriveUntil(() => test.Body.Sent.Contains(PlayingOperations.VendorClose) && test.Body.Pending is null, answer: true);
        test.Think();

        Assert.Equal(PlayingOperations.VendorOpen, test.Body.Sent[0]);
        Assert.Equal(PlayingOperations.VendorClose, test.Body.Sent[^1]);
        var trades = test.Body.Sent.Count - 2;
        Assert.InRange(trades, TraderRole.MinimumTradesPerVisit, TraderRole.MaximumTradesPerVisit);
        Assert.All(test.Actions.Where(action => action.Action == RealtimeCapabilityActionKind.VendorPurchase), action =>
            Assert.True(30 - action.ExpectedPrice >= TraderRole.CurrencyReserve, $"price {action.ExpectedPrice}"));
        Assert.Equal("idle", test.Role.State);
    }

    [Fact]
    public void AGathererRecoversItsCorpseAfterADeath()
    {
        var test = new RoleTest(PickaxeKit, body => new GathererRole(Veins, teleport: false, new Random(5), Town));
        test.Body.View.Items.Load(Inventory(4, Worn(Pickaxe, "tool")));
        test.Body.MoveTo(60f, -40f);
        test.Body.View.ApplyOwnerCombatState(OwnCombat(1));
        test.Think();
        test.Body.View.ApplyOwnerCombatState(OwnCombat(2, health: 0, lifeState: RealtimeCombatLifeState.Dead));
        test.Think();
        Assert.Equal("dead", test.Role.State);
        test.Body.View.ApplyOwnerCombatState(OwnCombat(3, lifeSequence: 2));
        test.Body.MoveTo(0f, -12f);
        test.Body.View.Items.Load(Inventory(5));
        var corpse = Guid.NewGuid();
        test.Body.View.ApplyCorpsePresence(Corpses(1, PlayerCorpse(corpse, 60f, -40f)));

        test.DriveUntilPending();
        Assert.Equal(PlayingOperations.LootOpen, test.Body.Pending);
        Assert.Equal(corpse, test.Body.OpenedCorpse);
        test.Answer();
        test.Body.View.ApplyCorpseInteractionResult(CorpseResult(corpse, RealtimeCorpseInteractionKind.Open));
        test.Body.View.ApplyCorpseViewState(View(corpse, 5));
        test.Think();
        Assert.Equal(PlayingOperations.LootAll, test.Body.Pending);
        test.Answer();
        test.Body.View.Items.Load(Inventory(6, Carried(Pickaxe, 0)));
        test.Body.View.ApplyCorpseViewClosed(new RealtimeCorpseViewClosed(corpse, "corpse_empty", "empty"));
        test.Think();
        test.Think();

        Assert.Equal(1, test.Body.Counters[PlayingCounter.CorpsesRecovered]);
        Assert.Equal(PlayingOperations.Equip, test.Body.Pending);
    }

    // The body builds every capability action the roles send so that the protocol accepts it,
    // as the Unity client does.
    [Fact]
    public void EveryCapabilityActionOfTheRolesEncodes()
    {
        var node = NodeAt(FirstNode, 62f, -40f);
        var vendor = VendorAt(VendorEntity, 0f, 6f);
        var gathering = Opened(node, RealtimeWorldActorCapabilityKind.Gathering, "resource.mine", "resource.iron_vein");
        var trading = Opened(vendor, RealtimeWorldActorCapabilityKind.Vendor, "services.vendor", PlayingTestWorld.Vendor);
        var pelts = Carried(Pelt, 0, 10);
        (RealtimeWorldInteractionOpened Open, PlayingCapabilityAction Action)[] actions =
        [
            (gathering, new PlayingCapabilityAction(RealtimeWorldActorCapabilityKind.Gathering, RealtimeCapabilityActionKind.StartGathering)),
            (trading, new PlayingCapabilityAction(RealtimeWorldActorCapabilityKind.Vendor, RealtimeCapabilityActionKind.VendorSale, Item: pelts, Quantity: 10, ExpectedPrice: 60)),
            (trading, new PlayingCapabilityAction(RealtimeWorldActorCapabilityKind.Vendor, RealtimeCapabilityActionKind.VendorPurchase, OfferDefinitionId: Arrow, Quantity: 100, ExpectedPrice: 100))
        ];

        foreach (var (open, action) in actions)
        {
            var intent = PlayingBotBody.CreateCapabilityIntent(Guid.NewGuid(), open, 1, open.Capabilities[0], action, 7);

            Assert.True(RealtimeProtocol.TryDecodeWorldInteractionIntent(
                RealtimeProtocol.EncodeWorldInteractionIntent(intent),
                out var decoded,
                out var error), error);
            Assert.Equal(action.Action, decoded.CapabilityAction);
            Assert.Equal(action.Action == RealtimeCapabilityActionKind.StartGathering ? 0 : 7, decoded.ExpectedCharacterRevision);
        }
    }

    private sealed class RoleTest
    {
        private TimeSpan now;
        private RealtimeWorldActorSpawn? vendor;

        public RoleTest(PlayingKit kit, Func<FakePlayingBody, IPlayingRole> create)
        {
            Body = new FakePlayingBody(kit);
            Role = create(Body);
        }

        public FakePlayingBody Body { get; }

        public IPlayingRole Role { get; }

        public List<PlayingCapabilityAction> Actions { get; } = [];

        public void Think(TimeSpan? step = null)
        {
            now += step ?? TimeSpan.FromMilliseconds(400);
            Role.Think(Body, now);
            Body.Events.Clear();
            if (Body.LastAction is { } action && (Actions.Count == 0 || !ReferenceEquals(Actions[^1], action)))
            {
                Actions.Add(action);
            }
        }

        public void ShowVendor()
        {
            vendor = VendorAt(VendorEntity, 0f, 6f);
            Body.View.ApplyWorldActorSpawn(vendor);
        }

        public RealtimeWorldActorSpawn AddNode(ulong entityId, float x, float z)
        {
            var node = NodeAt(entityId, x, z);
            Body.View.ApplyWorldActorSpawn(node);
            return node;
        }

        public void AddMob(ulong entityId, float x, float z, string definitionId)
        {
            Body.View.ApplyWorldActorSpawn(Mob(entityId, x, z, definitionId));
            Body.View.ApplyCombatantState(Combatant(entityId, 1));
        }

        // Answers the pending intent as the worker does: an open vendor opens the view, a
        // close closes it.
        public void Answer(RealtimeWorldInteractionOpened? opened = null)
        {
            var operation = Body.Pending;
            Body.Answer();
            switch (operation)
            {
                case PlayingOperations.VendorOpen when vendor is not null:
                    Body.View.ApplyWorldInteractionOpened(
                        opened ?? Opened(vendor, RealtimeWorldActorCapabilityKind.Vendor, "services.vendor", PlayingTestWorld.Vendor));
                    break;
                case PlayingOperations.GatherOpen when opened is not null:
                    Body.View.ApplyWorldInteractionOpened(opened);
                    break;
                case PlayingOperations.VendorClose:
                case PlayingOperations.GatherClose:
                    if (Body.View.OpenInteraction is { } open)
                    {
                        Body.View.ApplyWorldInteractionClosed(Closed(open, "world_interaction_closed"));
                    }

                    break;
            }
        }

        // Thinks, and finishes every walk at its destination, until an intent is pending.
        public void DriveUntilPending(int maximumThinks = 120)
        {
            DriveUntil(() => Body.Pending is not null, maximumThinks: maximumThinks);
        }

        public void DriveUntil(Func<bool> done, bool answer = false, int maximumThinks = 200)
        {
            for (var think = 0; think < maximumThinks; think++)
            {
                if (done())
                {
                    return;
                }

                if (Body.Pending is not null)
                {
                    Assert.True(answer, $"Unanswered intent {Body.Pending} in state {Role.State}.");
                    Answer();
                }
                else if (Body.MoveState is PlayingMoveState.Walking && Body.Walks.Count > 0)
                {
                    var destination = Body.Walks[^1];
                    Body.MoveTo(destination.X, destination.Z);
                    Body.MoveState = PlayingMoveState.Arrived;
                }

                Think(TimeSpan.FromSeconds(1));
            }

            Assert.True(done(), $"The role stopped in state {Role.State}.");
        }
    }
}
