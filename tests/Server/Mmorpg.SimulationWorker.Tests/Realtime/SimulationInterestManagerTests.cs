using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The incremental interest index against a model that recomputes every rule from all positions:
// entities walk across cells, jump, join, leave, and drop out of the index and come back, and
// every refresh and join must give what the model gives: the visible entities, their tiers, the
// detail, the viewers of every entity, and the states that viewers missed.
public sealed class SimulationInterestManagerTests
{
    private const int Connections = 30;
    private const float Area = 600f;
    private const uint Slots = 15;

    private static readonly InterestManagementConfig Config = new(64f, 128f, 144f, 3, 50f, 58f, 70f, 78f);

    [Fact]
    public void TheIncrementalIndexMatchesAModelOfTheRulesOverALongRandomWalk()
    {
        RunRandomWalk(SimulationInterestManager.MaximumEnteredPerRefresh, 5_000, varyingRoom: false);
    }

    // With a budget of three entries per refresh, the crowd around a connection enters nearest
    // first over several refreshes, and the model of the rules, with the same budget, agrees on
    // every refresh, join, viewer, and pair.
    [Fact]
    public void ABudgetOfEntriesKeepsTheIndexEqualToAModelOverALongRandomWalk()
    {
        RunRandomWalk(3, 1_000, varyingRoom: false);
    }

    // The room of a connection's reliable queue changes between its refreshes, from none to more
    // than a burst, and the index agrees with the model on every refresh, join, viewer, pair, and
    // count of entities that waited, by what made them wait.
    [Fact]
    public void ARoomThatChangesAtEveryRefreshKeepsTheIndexEqualToAModelOverALongRandomWalk()
    {
        RunRandomWalk(6, 1_000, varyingRoom: true);
    }

    // The crowd of ten around a connection with a budget of four enters nearest first over three
    // refreshes. An entity that waits is not in the view at all: no viewer pair, no recipient of
    // any change, no detail, and no despawn when it leaves before it ever entered; and an entity
    // that leaves the view is never held back by the budget.
    [Fact]
    public void AnEntityThatWaitsForItsEntryIsNotInTheViewAndNothingHoldsBackAnExit()
    {
        var manager = new SimulationInterestManager(Config, 4);

        // Entity 2 stands 100 m away, 3 at 90 m, and so on to entity 11 at 10 m, and entity 1
        // is the observer.
        var places = Enumerable.Range(2, 10)
            .Select(id => new SimulationInterestEntity((ulong)id, 10f * (12 - id), 0f))
            .Prepend(new SimulationInterestEntity(1, 0f, 0f))
            .ToArray();
        manager.Synchronize(places);

        // The observer itself and the three nearest others.
        var first = manager.Refresh(1, 1);
        Assert.Equal([1ul, 9ul, 10ul, 11ul], first.Entered);
        Assert.Equal(first.Entered, manager.GetVisibleOrdered(1));
        Assert.Equal(4, manager.ViewerPairCount);
        Assert.Equal(7, manager.DeferredEntryCount);

        var recipients = new List<int>();
        foreach (var waiting in new ulong[] { 2, 3, 4, 5, 6, 7, 8 })
        {
            Assert.False(manager.IsVisibleToAnyConnection(waiting));
            Assert.False(manager.IsInDetail(1, waiting));
            Assert.DoesNotContain(waiting, manager.GetVisible(1));
            manager.CollectRecipients(waiting, MissedStates.Combatant, true, -1, recipients);
            Assert.Empty(recipients);
            Assert.Equal(MissedStates.None, manager.TakeMissed(1, waiting));
        }

        Assert.True(manager.TryGetVisibleSpans(1, out var orderedVisible, out var orderedNear, out _));
        Assert.Equal(first.Entered, orderedVisible.ToArray());
        Assert.Equal(orderedVisible.Length, orderedNear.Length);

        // The entity that waits at 80 m leaves for 200 m, beyond the exit radius, before it ever
        // entered: it never appears as exited. Entity 9, in the view, leaves at the same time and
        // goes in this refresh whatever the budget does.
        manager.Synchronize(places
            .Select(place => place.EntityId switch
            {
                4 => place with { X = 200f },
                9 => place with { X = 300f },
                _ => place
            })
            .ToArray());
        var second = manager.Refresh(1, 1);
        Assert.Equal([9ul], second.Exited);
        Assert.Equal([5ul, 6ul, 7ul, 8ul], second.Entered);
        Assert.DoesNotContain(4ul, manager.GetVisibleOrdered(1));
        Assert.Equal(7, manager.ViewerPairCount);

        var third = manager.Refresh(1, 1);
        Assert.Equal([2ul, 3ul], third.Entered);
        Assert.Empty(third.Exited);
        Assert.Equal([1ul, 2ul, 3ul, 5ul, 6ul, 7ul, 8ul, 10ul, 11ul], manager.GetVisibleOrdered(1));
        Assert.Equal(9, manager.ViewerPairCount);
        Assert.Empty(manager.Refresh(1, 1).Entered);
    }

    // A join enters the connections that see its place at once, while their entries since their
    // last refresh are within the budget; after that the entity waits for the connection's next
    // refresh, which has the budget again.
    [Fact]
    public void AJoinEntersAtOnceWithinTheBudgetAndOtherwiseAtTheNextRefresh()
    {
        var manager = new SimulationInterestManager(Config, 3);
        manager.AddEntity(new SimulationInterestEntity(1, 0f, 0f));
        Assert.Equal([1ul], manager.Refresh(1, 1).Entered);

        // Entries 2 and 3 fill the window with the connection's own, and the next ones wait.
        Assert.Equal([1], manager.AddEntity(new SimulationInterestEntity(2, 10f, 0f)));
        Assert.Equal([1], manager.AddEntity(new SimulationInterestEntity(3, 20f, 0f)));
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(4, 5f, 0f)));
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(5, 30f, 0f)));
        Assert.Equal([1ul, 2ul, 3ul], manager.GetVisibleOrdered(1));
        Assert.Equal(2, manager.DeferredEntryCount);

        // The next refresh brings the nearest of them first, a budget of them.
        var refresh = manager.Refresh(1, 1);
        Assert.Equal([4ul, 5ul], refresh.Entered);
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul], manager.GetVisibleOrdered(1));
        Assert.Equal(2, manager.DeferredEntryCount);

        // That refresh used two of the budget, so one more join fits and the next waits.
        Assert.Equal([1], manager.AddEntity(new SimulationInterestEntity(6, 40f, 0f)));
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(7, 41f, 0f)));
        Assert.Equal(3, manager.DeferredEntryCount);
        Assert.Equal([7ul], manager.Refresh(1, 1).Entered);
    }

    // The connection's own entity comes before every other, also when another entity stands at
    // the very place, so a connection never lacks the entity that it controls.
    [Fact]
    public void TheConnectionsOwnEntityEntersBeforeEntitiesAtTheSamePlace()
    {
        var manager = new SimulationInterestManager(Config, 2);
        manager.Synchronize([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 0f, 0f),
            new SimulationInterestEntity(3, 0f, 0f),
            new SimulationInterestEntity(4, 0f, 0f)
        ]);

        // Entity 4 is the observer; the others enter by id, two at a time after it.
        Assert.Equal([1ul, 4ul], manager.Refresh(7, 4).Entered);
        Assert.Equal([2ul, 3ul], manager.Refresh(7, 4).Entered);
        Assert.Equal([1ul, 2ul, 3ul, 4ul], manager.GetVisibleOrdered(7));
    }

    // An entity whose spawn could not be built stays out of the view while it is within the enter
    // radius, so it is not built and failed again at every refresh, and it is a new entity again
    // once it was beyond the enter radius at a refresh.
    [Fact]
    public void AWithheldEntityStaysOutWhileItIsWithinTheEnterRadiusAndIsNewAfterItLeft()
    {
        var manager = new SimulationInterestManager(Config);
        manager.Synchronize([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 10f, 0f),
            new SimulationInterestEntity(3, 20f, 0f)
        ]);
        manager.Refresh(1, 1);
        manager.MarkMissed(1, 2, MissedStates.Look);

        Assert.True(manager.Withhold(1, 2));
        Assert.False(manager.Withhold(1, 2));
        Assert.Equal([1ul, 3ul], manager.GetVisibleOrdered(1));
        Assert.False(manager.IsVisibleToAnyConnection(2));
        Assert.Equal(2, manager.ViewerPairCount);
        Assert.Equal(MissedStates.None, manager.TakeMissed(1, 2));

        // It stays out at every refresh while it is within the enter radius.
        Assert.Empty(manager.Refresh(1, 1).Entered);

        // A refresh that finds it between the enter and the exit radius, where an entity that was
        // not seen does not enter, forgets that it was withheld.
        manager.Synchronize([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 138f, 0f),
            new SimulationInterestEntity(3, 20f, 0f)
        ]);
        Assert.Empty(manager.Refresh(1, 1).Entered);

        // Within the enter radius again, it enters.
        manager.Synchronize([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 10f, 0f),
            new SimulationInterestEntity(3, 20f, 0f)
        ]);
        Assert.Equal([2ul], manager.Refresh(1, 1).Entered);
    }

    // A connection whose reliable queue has no room takes in nothing but its own entity, joins
    // included, and takes the nearest of what waits in as the room comes back, never more than a
    // burst, whatever a source that is out of range says.
    [Fact]
    public void ANoRoomLetsOnlyTheConnectionsOwnEntityInAndAChangingRoomIsFollowedRefreshByRefresh()
    {
        var manager = new SimulationInterestManager(Config, 4);
        var room = 0;
        manager.EntriesAllowedSource = _ => room;
        manager.Synchronize(Enumerable.Range(1, 6)
            .Select(id => new SimulationInterestEntity((ulong)id, 10f * (id - 1), 0f))
            .ToArray());

        // Entity 1 is the observer: it enters, and of the five others two are beyond a burst of
        // four anyway and three wait because of the queue.
        var first = manager.Refresh(1, 1);
        Assert.Equal([1ul], first.Entered);
        Assert.Equal((2, 3), (manager.DeferredByBurstCount, manager.DeferredByQueueCount));
        Assert.Empty(manager.Refresh(1, 1).Entered);
        Assert.Equal(10, manager.DeferredEntryCount);

        // A join waits too, and the room that returns takes the nearest of those that wait in.
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(7, 5f, 0f)));
        Assert.Equal(11, manager.DeferredEntryCount);
        room = 2;
        var second = manager.Refresh(1, 1);
        Assert.Equal([2ul, 7ul], second.Entered);
        Assert.Equal([7ul, 2ul], second.EnteredNearestFirst);

        // A room that is none again, and one beyond a burst, which a burst limits.
        room = 0;
        Assert.Empty(manager.Refresh(1, 1).Entered);
        room = int.MaxValue;
        Assert.Equal([3ul, 4ul, 5ul, 6ul], manager.Refresh(1, 1).Entered);
        room = -5;
        Assert.Empty(manager.Refresh(1, 1).Entered);
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul, 6ul, 7ul], manager.GetVisibleOrdered(1));
    }

    // The source is asked once per refresh of the connection, and a join between two refreshes
    // enters within what the last refresh allowed, not what the queue would allow by then.
    [Fact]
    public void TheRoomIsAskedOncePerRefreshAndJoinsUseWhatTheLastRefreshAllowed()
    {
        var manager = new SimulationInterestManager(Config, 4);
        var asked = new List<int>();
        var room = 2;
        manager.EntriesAllowedSource = connectionId =>
        {
            asked.Add(connectionId);
            return room;
        };
        manager.AddEntity(new SimulationInterestEntity(1, 0f, 0f));
        manager.AddEntity(new SimulationInterestEntity(9, 0f, 5f));
        Assert.Empty(asked);

        // The first refresh lets in the connection's own entity and one more, and its room is two.
        Assert.Equal([1ul, 9ul], manager.Refresh(1, 1).Entered);
        Assert.Equal([1], asked);

        // The queue has room for three by now, but the joins follow the refresh: none fits.
        room = 3;
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(2, 10f, 0f)));
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(3, 20f, 0f)));
        Assert.Equal([1], asked);
        Assert.Equal((0, 2), (manager.DeferredByBurstCount, manager.DeferredByQueueCount));

        // The next refresh asks again and takes the room it is told, two entities of three; one
        // more join fits in it and the next waits.
        Assert.Equal([2ul, 3ul], manager.Refresh(1, 1).Entered);
        Assert.Equal([1, 1], asked);
        Assert.Equal([1], manager.AddEntity(new SimulationInterestEntity(4, 30f, 0f)));
        Assert.Empty(manager.AddEntity(new SimulationInterestEntity(5, 40f, 0f)));
        Assert.Equal([5ul], manager.Refresh(1, 1).Entered);
    }

    // Entities that wait are counted by what made them wait: more than one burst holds, or a
    // queue with less room than a burst, so a slow client can be told from a big crowd.
    [Fact]
    public void EntitiesThatWaitAreCountedByTheBurstOrByTheQueue()
    {
        var manager = new SimulationInterestManager(Config, 4);
        var room = 100;
        manager.EntriesAllowedSource = _ => room;
        manager.Synchronize(Enumerable.Range(1, 11)
            .Select(id => new SimulationInterestEntity((ulong)id, 10f * (id - 1), 0f))
            .ToArray());

        // Eleven candidates, a burst of four: seven wait because of the burst alone.
        Assert.Equal(4, manager.Refresh(1, 1).Entered.Count);
        Assert.Equal((7, 0), (manager.DeferredByBurstCount, manager.DeferredByQueueCount));

        // Seven candidates, room for one: three of them would have entered in a burst, and
        // wait because of the queue; the others are beyond the burst anyway.
        room = 1;
        Assert.Single(manager.Refresh(1, 1).Entered);
        Assert.Equal((10, 3), (manager.DeferredByBurstCount, manager.DeferredByQueueCount));
    }

    private static void RunRandomWalk(int budget, int minimumChanges, bool varyingRoom)
    {
        var random = new Random(4242);
        var manager = new SimulationInterestManager(Config, budget);
        var model = new Model(budget);

        // What the source says for the connection's next refresh; beyond what a burst holds, and
        // below none, as a source that is out of range would say it.
        var room = new Dictionary<int, int>();
        manager.EntriesAllowedSource = connectionId => room.GetValueOrDefault(connectionId, budget);
        var positions = new Dictionary<ulong, (float X, float Z)>();
        for (ulong entityId = 1; entityId <= 120; entityId++)
        {
            positions[entityId] = (Next(random, 0f, Area), Next(random, 0f, Area));
        }

        var dropped = new HashSet<ulong>();
        var nextId = 121ul;
        var refreshes = 0;
        var changes = 0;
        var detailEntered = 0;
        var takenOnSlots = 0;
        var recipients = new List<int>();
        var missedOnSlot = new List<SimulationInterestMissed>();
        for (var step = 0; step < 2_000; step++)
        {
            foreach (var entityId in positions.Keys.ToArray())
            {
                var (x, z) = positions[entityId];
                var jump = random.NextDouble() < 0.01;
                x = Math.Clamp(x + Next(random, jump ? -200f : -12f, jump ? 200f : 12f), 0f, Area);
                z = Math.Clamp(z + Next(random, jump ? -200f : -12f, jump ? 200f : 12f), 0f, Area);
                positions[entityId] = (x, z);
            }

            // Now and then an entity that no connection controls leaves the index for a while.
            if (random.NextDouble() < 0.05)
            {
                var candidates = positions.Keys.Where(entityId => entityId > Connections && !dropped.Contains(entityId)).ToArray();
                dropped.Add(candidates[random.Next(candidates.Length)]);
            }
            else if (dropped.Count > 0 && random.NextDouble() < 0.1)
            {
                dropped.Remove(dropped.First());
            }

            var listed = positions
                .Where(pair => !dropped.Contains(pair.Key))
                .OrderBy(pair => pair.Key)
                .Select(pair => new SimulationInterestEntity(pair.Key, pair.Value.X, pair.Value.Z))
                .ToArray();
            manager.Synchronize(listed);
            model.Synchronize(listed);
            Assert.Equal(listed.Length, manager.EntityCount);

            // Joins and despawns between the snapshots.
            if (random.NextDouble() < 0.2)
            {
                var joined = new SimulationInterestEntity(nextId++, Next(random, 0f, Area), Next(random, 0f, Area));
                positions[joined.EntityId] = (joined.X, joined.Z);
                Assert.Equal(model.Add(joined), manager.AddEntity(joined));
            }

            if (random.NextDouble() < 0.1)
            {
                var candidates = positions.Keys.Where(entityId => entityId > Connections).ToArray();
                var forgotten = candidates[random.Next(candidates.Length)];
                positions.Remove(forgotten);
                dropped.Remove(forgotten);
                Assert.Equal(model.Forget(forgotten), manager.ForgetEntity(forgotten).Order());
            }

            // Replicators hold back states from some viewers, also from ones that see the entity
            // in detail, as a change right before a refresh does.
            for (var mark = 0; mark < 20; mark++)
            {
                var connectionId = random.Next(1, Connections + 1);
                var visible = manager.GetVisibleOrdered(connectionId);
                if (visible.Count == 0)
                {
                    continue;
                }

                var entityId = visible[random.Next(visible.Count)];
                manager.MarkMissed(connectionId, entityId, MissedStates.Look);
                model.MarkMissed(connectionId, entityId);
            }

            for (var connectionId = 1; connectionId <= Connections; connectionId++)
            {
                if (connectionId % 3 != step % 3)
                {
                    continue;
                }

                room[connectionId] = varyingRoom ? random.Next(-1, budget + 3) : budget;
                var update = manager.Refresh(connectionId, (ulong)connectionId);
                var expected = model.Refresh(connectionId, (ulong)connectionId, room[connectionId]);
                Assert.Equal(expected.Entered, update.Entered);
                Assert.Equal(expected.EnteredNearestFirst, update.EnteredNearestFirst);
                Assert.Equal(expected.Exited, update.Exited);
                Assert.Equal(expected.Visible, manager.GetVisibleOrdered(connectionId));
                Assert.Equal(expected.Near, manager.GetNearOrdered(connectionId));
                Assert.Equal(expected.Detail, expected.Visible.Select(entityId => manager.IsInDetail(connectionId, entityId)));
                Assert.Equal(expected.DetailEntered, update.DetailEntered);
                Assert.True(update.Visible.SetEquals(expected.Visible));

                // The replicator sends what came into detail, so it is missed no longer.
                foreach (var entityId in update.DetailEntered)
                {
                    Assert.Equal(MissedStates.Look, manager.TakeMissed(connectionId, entityId));
                    model.TakeMissed(connectionId, entityId);
                }

                refreshes++;
                changes += expected.Entered.Count + expected.Exited.Count;
                detailEntered += expected.DetailEntered.Count;
            }

            // The slot of this step takes what the viewers of its entities missed.
            var slotPhase = (uint)(step % Slots);
            manager.TakeMissedOnSlot(slotPhase, Slots, missedOnSlot);
            Assert.Equal(
                model.TakeMissedOnSlot(slotPhase, Slots),
                missedOnSlot.Select(missed => (missed.ConnectionId, missed.EntityId)).Order());
            Assert.All(missedOnSlot, missed => Assert.Equal(MissedStates.Look, missed.States));
            takenOnSlots += missedOnSlot.Count;

            // Every entity's viewers are the connections that see it.
            foreach (var entityId in positions.Keys)
            {
                manager.CollectRecipients(entityId, MissedStates.None, true, -1, recipients);
                Assert.Equal(model.ViewersOf(entityId), recipients.Order());
                Assert.Equal(recipients.Count > 0, manager.IsVisibleToAnyConnection(entityId));
            }

            Assert.Equal(model.PairCount, manager.ViewerPairCount);
        }

        // The walk crossed cells and radii often enough to matter.
        Assert.Equal(20_000, refreshes);
        Assert.InRange(changes, minimumChanges, int.MaxValue);
        Assert.InRange(detailEntered, 100, int.MaxValue);
        Assert.InRange(takenOnSlots, 1_000, int.MaxValue);

        // A budget of three made entities wait, and the default budget never does here.
        Assert.Equal(model.DeferredByBurst, manager.DeferredByBurstCount);
        Assert.Equal(model.DeferredByQueue, manager.DeferredByQueueCount);
        if (budget < 10)
        {
            Assert.InRange(manager.DeferredEntryCount, 1_000, long.MaxValue);
            Assert.Equal(varyingRoom, manager.DeferredByQueueCount > 0);
        }
        else
        {
            Assert.Equal(0, manager.DeferredEntryCount);
        }
    }

    // A connection sees its own entity, its target, and whoever targets its entity in detail at
    // any distance; a change of a state reaches it at once then, and the others missed it.
    [Fact]
    public void TheTargetRelationsKeepAViewerInDetailBeyondTheDetailRadius()
    {
        var manager = new SimulationInterestManager(Config);
        manager.Synchronize([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 100f, 0f),
            new SimulationInterestEntity(3, 0f, 100f),
            new SimulationInterestEntity(4, 60f, 0f)
        ]);
        manager.Refresh(1, 1);
        Assert.Equal([true, false, false, true], new ulong[] { 1, 2, 3, 4 }.Select(entityId => manager.IsInDetail(1, entityId)));

        // Entity 1 targets 2, and 3 targets 1.
        manager.SetTarget(1, 2);
        manager.SetTarget(3, 1);
        Assert.Equal([true, true, true, true], new ulong[] { 1, 2, 3, 4 }.Select(entityId => manager.IsInDetail(1, entityId)));
        var recipients = new List<int>();
        manager.CollectRecipients(2, MissedStates.Combatant, false, -1, recipients);
        Assert.Equal([1], recipients);
        Assert.Equal(MissedStates.None, manager.TakeMissed(1, 2));

        // The targets end, and the far entities are out of detail again: a change of them is
        // missed until it is taken.
        manager.SetTarget(1, 0);
        manager.SetTarget(3, 0);
        manager.CollectRecipients(2, MissedStates.Combatant, false, -1, recipients);
        Assert.Empty(recipients);
        manager.CollectRecipients(3, MissedStates.WorldActor, false, -1, recipients);
        Assert.Empty(recipients);
        Assert.Equal(MissedStates.Combatant, manager.TakeMissed(1, 2));
        Assert.Equal(MissedStates.None, manager.TakeMissed(1, 2));

        // An urgent change reaches every viewer and leaves nothing missed.
        manager.CollectRecipients(3, MissedStates.WorldActor, true, -1, recipients);
        Assert.Equal([1], recipients);
        Assert.Equal(MissedStates.None, manager.TakeMissed(1, 3));

        // A target set before the connection's first refresh holds from it on.
        manager.SetTarget(5, 2);
        manager.Synchronize([
            new SimulationInterestEntity(1, 0f, 0f),
            new SimulationInterestEntity(2, 100f, 0f),
            new SimulationInterestEntity(3, 0f, 100f),
            new SimulationInterestEntity(4, 60f, 0f),
            new SimulationInterestEntity(5, 0f, 0f)
        ]);
        manager.Refresh(5, 5);
        Assert.True(manager.IsInDetail(5, 2));
        Assert.False(manager.IsInDetail(5, 3));
    }

    private static float Next(Random random, float minimum, float maximum)
    {
        return minimum + ((float)random.NextDouble() * (maximum - minimum));
    }

    // The rules computed from all positions, with sets: visible within the enter radius or within
    // the exit radius when visible before, near within the near radius or within the near exit
    // radius when near before, and in detail the same way with the detail radii. A pair that left
    // the view forgets what it missed. A connection controls the entity with its id.
    // The budget is the most entities that enter a connection between two of its refreshes, the
    // refresh included: the nearest first, the connection's own entity before any other.
    // The room that the last refresh of the connection was told of, at least 0 and at most the
    // budget, limits the joins until its next refresh, and the connection's own entity enters
    // whatever it is.
    private sealed class Model(int budget)
    {
        private readonly Dictionary<int, ConnectionModel> connections = [];
        private Dictionary<ulong, (float X, float Z)> indexed = [];

        // The entities that waited, by what made them wait.
        public long DeferredByBurst { get; private set; }

        public long DeferredByQueue { get; private set; }

        public int PairCount => connections.Values.Sum(state => state.Visible.Count);

        public void Synchronize(IEnumerable<SimulationInterestEntity> entities)
        {
            indexed = entities.ToDictionary(entity => entity.EntityId, entity => (entity.X, entity.Z));
        }

        public List<int> Add(SimulationInterestEntity entity)
        {
            indexed.Add(entity.EntityId, (entity.X, entity.Z));
            var entered = new List<int>();
            foreach (var (connectionId, state) in connections)
            {
                var distance = DistanceSquared((ulong)connectionId, entity.EntityId);
                if (distance <= Config.EnterRadius * Config.EnterRadius && state.Entered >= state.Room)
                {
                    if (state.Room < budget)
                    {
                        DeferredByQueue++;
                    }
                    else
                    {
                        DeferredByBurst++;
                    }

                    continue;
                }

                if (distance <= Config.EnterRadius * Config.EnterRadius
                    && state.Visible.Add(entity.EntityId))
                {
                    state.Entered++;
                    if (distance <= Config.NearRadius * Config.NearRadius)
                    {
                        state.Near.Add(entity.EntityId);
                    }

                    if (distance <= Config.DetailRadius * Config.DetailRadius)
                    {
                        state.Detail.Add(entity.EntityId);
                    }

                    entered.Add(connectionId);
                }
            }

            entered.Sort();
            return entered;
        }

        public List<int> Forget(ulong entityId)
        {
            indexed.Remove(entityId);
            var affected = new List<int>();
            foreach (var (connectionId, state) in connections)
            {
                state.Near.Remove(entityId);
                state.Detail.Remove(entityId);
                state.Missed.Remove(entityId);
                if (state.Visible.Remove(entityId))
                {
                    affected.Add(connectionId);
                }
            }

            affected.Sort();
            return affected;
        }

        public void MarkMissed(int connectionId, ulong entityId)
        {
            connections[connectionId].Missed.Add(entityId);
        }

        public void TakeMissed(int connectionId, ulong entityId)
        {
            connections[connectionId].Missed.Remove(entityId);
        }

        public List<(int ConnectionId, ulong EntityId)> TakeMissedOnSlot(uint slotPhase, uint slots)
        {
            var taken = new List<(int ConnectionId, ulong EntityId)>();
            foreach (var (connectionId, state) in connections)
            {
                foreach (var entityId in state.Missed.Where(entityId => entityId % slots == slotPhase).ToArray())
                {
                    state.Missed.Remove(entityId);
                    taken.Add((connectionId, entityId));
                }
            }

            return taken.Order().ToList();
        }

        public List<int> ViewersOf(ulong entityId)
        {
            return connections.Where(pair => pair.Value.Visible.Contains(entityId)).Select(pair => pair.Key).Order().ToList();
        }

        public RefreshResult Refresh(int connectionId, ulong observerId, int room)
        {
            var allowed = Math.Clamp(room, 0, budget);
            if (!connections.TryGetValue(connectionId, out var state))
            {
                state = new ConnectionModel();
                connections.Add(connectionId, state);
            }

            var visible = new HashSet<ulong>();
            var near = new HashSet<ulong>();
            var detail = new HashSet<ulong>();
            var entering = new List<(float Distance, ulong EntityId)>();
            foreach (var entityId in indexed.Keys)
            {
                var distance = DistanceSquared(observerId, entityId);
                var wasVisible = state.Visible.Contains(entityId);
                if (distance <= Config.EnterRadius * Config.EnterRadius
                    || (distance <= Config.ExitRadius * Config.ExitRadius && wasVisible))
                {
                    if (!wasVisible)
                    {
                        entering.Add((entityId == observerId ? -1f : distance, entityId));
                        continue;
                    }

                    visible.Add(entityId);
                    if (distance <= Config.NearRadius * Config.NearRadius
                        || (distance <= Config.NearExitRadius * Config.NearExitRadius && state.Near.Contains(entityId)))
                    {
                        near.Add(entityId);
                    }

                    if (distance <= Config.DetailRadius * Config.DetailRadius
                        || (distance <= Config.DetailExitRadius * Config.DetailExitRadius && state.Detail.Contains(entityId)))
                    {
                        detail.Add(entityId);
                    }
                }
            }

            // The nearest of those that wait enter, with the tiers that the distance gives them.
            var limit = Math.Max(allowed, entering.Any(candidate => candidate.EntityId == observerId) ? 1 : 0);
            var burst = Math.Min(entering.Count, budget);
            DeferredByBurst += entering.Count - burst;
            DeferredByQueue += Math.Max(0, burst - limit);
            var admitted = entering.OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.EntityId).Take(limit).ToList();
            foreach (var (_, entityId) in admitted)
            {
                var distance = DistanceSquared(observerId, entityId);
                visible.Add(entityId);
                if (distance <= Config.NearRadius * Config.NearRadius)
                {
                    near.Add(entityId);
                }

                if (distance <= Config.DetailRadius * Config.DetailRadius)
                {
                    detail.Add(entityId);
                }
            }

            state.Entered = admitted.Count;
            state.Room = allowed;
            var entered = visible.Except(state.Visible).Order().ToList();
            var exited = state.Visible.Except(visible).Order().ToList();
            var detailEntered = visible
                .Where(entityId => state.Visible.Contains(entityId)
                    && !state.Detail.Contains(entityId)
                    && detail.Contains(entityId)
                    && state.Missed.Contains(entityId))
                .Order()
                .ToList();
            state.Missed.ExceptWith(exited);
            state.Visible.Clear();
            state.Visible.UnionWith(visible);
            state.Near.Clear();
            state.Near.UnionWith(near);
            state.Detail.Clear();
            state.Detail.UnionWith(detail);
            var ordered = visible.Order().ToList();
            return new RefreshResult(
                entered,
                admitted.Select(candidate => candidate.EntityId).ToList(),
                exited,
                ordered,
                ordered.Select(near.Contains).ToList(),
                ordered.Select(detail.Contains).ToList(),
                detailEntered);
        }

        private float DistanceSquared(ulong first, ulong second)
        {
            var deltaX = indexed[second].X - indexed[first].X;
            var deltaZ = indexed[second].Z - indexed[first].Z;
            return (deltaX * deltaX) + (deltaZ * deltaZ);
        }
    }

    private sealed class ConnectionModel
    {
        public HashSet<ulong> Visible { get; } = [];

        public HashSet<ulong> Near { get; } = [];

        public HashSet<ulong> Detail { get; } = [];

        public HashSet<ulong> Missed { get; } = [];

        // The entities that entered since the last refresh, and by it, and what it allowed.
        public int Entered { get; set; }

        public int Room { get; set; }
    }

    private sealed record RefreshResult(
        List<ulong> Entered,
        List<ulong> EnteredNearestFirst,
        List<ulong> Exited,
        List<ulong> Visible,
        List<bool> Near,
        List<bool> Detail,
        List<ulong> DetailEntered);
}
