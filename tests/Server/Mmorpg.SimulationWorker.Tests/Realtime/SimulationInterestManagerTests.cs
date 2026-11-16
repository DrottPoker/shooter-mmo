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
        var random = new Random(4242);
        var manager = new SimulationInterestManager(Config);
        var model = new Model();
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

                var update = manager.Refresh(connectionId, (ulong)connectionId);
                var expected = model.Refresh(connectionId, (ulong)connectionId);
                Assert.Equal(expected.Entered, update.Entered);
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
        Assert.InRange(changes, 5_000, int.MaxValue);
        Assert.InRange(detailEntered, 100, int.MaxValue);
        Assert.InRange(takenOnSlots, 1_000, int.MaxValue);
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
    private sealed class Model
    {
        private readonly Dictionary<int, ConnectionModel> connections = [];
        private Dictionary<ulong, (float X, float Z)> indexed = [];

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
                if (distance <= Config.EnterRadius * Config.EnterRadius && state.Visible.Add(entity.EntityId))
                {
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

        public RefreshResult Refresh(int connectionId, ulong observerId)
        {
            if (!connections.TryGetValue(connectionId, out var state))
            {
                state = new ConnectionModel();
                connections.Add(connectionId, state);
            }

            var visible = new HashSet<ulong>();
            var near = new HashSet<ulong>();
            var detail = new HashSet<ulong>();
            foreach (var entityId in indexed.Keys)
            {
                var distance = DistanceSquared(observerId, entityId);
                var wasVisible = state.Visible.Contains(entityId);
                if (distance <= Config.EnterRadius * Config.EnterRadius
                    || (distance <= Config.ExitRadius * Config.ExitRadius && wasVisible))
                {
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
    }

    private sealed record RefreshResult(
        List<ulong> Entered,
        List<ulong> Exited,
        List<ulong> Visible,
        List<bool> Near,
        List<bool> Detail,
        List<ulong> DetailEntered);
}
