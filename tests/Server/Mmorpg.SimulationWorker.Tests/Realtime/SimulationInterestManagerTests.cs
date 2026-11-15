using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The incremental interest index against a model that recomputes every rule from all positions:
// entities walk across cells, jump, join, leave, and drop out of the index and come back, and
// every refresh and join must give what the model gives.
public sealed class SimulationInterestManagerTests
{
    private const int Connections = 30;
    private const float Area = 600f;

    private static readonly InterestManagementConfig Config = new(64f, 128f, 144f, 3, 48f, 56f);

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

            for (var connectionId = 1; connectionId <= Connections; connectionId++)
            {
                if (connectionId % 3 != step % 3)
                {
                    continue;
                }

                var update = manager.Refresh(connectionId, (ulong)connectionId);
                var (entered, exited, visible, near) = model.Refresh(connectionId, (ulong)connectionId);
                Assert.Equal(entered, update.Entered);
                Assert.Equal(exited, update.Exited);
                Assert.Equal(visible, manager.GetVisibleOrdered(connectionId));
                Assert.Equal(near, manager.GetNearOrdered(connectionId));
                Assert.True(update.Visible.SetEquals(visible));
                refreshes++;
                changes += entered.Count + exited.Count;
            }
        }

        // The walk crossed cells and radii often enough to matter.
        Assert.Equal(20_000, refreshes);
        Assert.InRange(changes, 5_000, int.MaxValue);
    }

    private static float Next(Random random, float minimum, float maximum)
    {
        return minimum + ((float)random.NextDouble() * (maximum - minimum));
    }

    // The rules computed from all positions, with sets: visible within the enter radius or within
    // the exit radius when visible before, near within the near radius or within the near exit
    // radius when near before. A connection controls the entity with its id.
    private sealed class Model
    {
        private readonly Dictionary<int, (HashSet<ulong> Visible, HashSet<ulong> Near)> connections = [];
        private Dictionary<ulong, (float X, float Z)> indexed = [];

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
                if (state.Visible.Remove(entityId))
                {
                    affected.Add(connectionId);
                }
            }

            affected.Sort();
            return affected;
        }

        public (List<ulong> Entered, List<ulong> Exited, List<ulong> Visible, List<bool> Near) Refresh(int connectionId, ulong observerId)
        {
            if (!connections.TryGetValue(connectionId, out var state))
            {
                state = ([], []);
                connections.Add(connectionId, state);
            }

            var visible = new HashSet<ulong>();
            var near = new HashSet<ulong>();
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
                }
            }

            var entered = visible.Except(state.Visible).Order().ToList();
            var exited = state.Visible.Except(visible).Order().ToList();
            state.Visible.Clear();
            state.Visible.UnionWith(visible);
            state.Near.Clear();
            state.Near.UnionWith(near);
            var ordered = visible.Order().ToList();
            return (entered, exited, ordered, ordered.Select(near.Contains).ToList());
        }

        private float DistanceSquared(ulong first, ulong second)
        {
            var deltaX = indexed[second].X - indexed[first].X;
            var deltaZ = indexed[second].Z - indexed[first].Z;
            return (deltaX * deltaX) + (deltaZ * deltaZ);
        }
    }
}
