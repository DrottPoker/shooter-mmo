using Mmorpg.Client.Worlds;
using Mmorpg.GameSimulation;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The collision chunks around the local player: a refresh, which runs on every owner
    // snapshot, loads the chunks around its anchor and unloads those beyond the unload radius,
    // and the check before every predicted tick loads them; both count each change in the
    // stream's revision and allocate nothing when no chunk loads.
    public sealed class CollisionStreamingTests
    {
        [Test]
        public void ARefreshLoadsAroundItsAnchorAndAllocatesNothingWhenNothingChanges()
        {
            Assert.That(
                UnityWorldCollisionLoader.TryCreateStream(RealtimeHubMessages.WorldId, out var stream, out var error),
                Is.True,
                error);
            var origin = new[] { new SimulationVector3(0f, 0f, 0f) };
            var far = new[] { new SimulationVector3(5000f, 0f, 5000f) };

            Assert.That(stream.TryRefresh(origin, out error), Is.True, error);
            var loaded = stream.CollisionWorld.ChunkCount;
            Assert.That(loaded, Is.GreaterThan(0));
            Assert.That(stream.CollisionWorld.IsChunkLoaded(0, 0), Is.True);
            Assert.That(stream.Revision, Is.EqualTo(loaded));

            var refreshed = true;
            Assert.That(ManagedAllocations.Of(() => refreshed &= stream.TryRefresh(origin, out _)), Is.Zero);
            Assert.That(refreshed, Is.True);
            Assert.That(stream.Revision, Is.EqualTo(loaded), "nothing loaded or unloaded");

            // Far away every chunk unloads, and back at the start they load again.
            Assert.That(stream.TryRefresh(far, out error), Is.True, error);
            Assert.That(stream.CollisionWorld.ChunkCount, Is.Zero);
            Assert.That(stream.Revision, Is.EqualTo(2 * loaded));
            Assert.That(stream.TryRefresh(origin, out error), Is.True, error);
            Assert.That(stream.CollisionWorld.ChunkCount, Is.EqualTo(loaded));

            Assert.That(stream.TryRefresh(new[] { new SimulationVector3(float.NaN, 0f, 0f) }, out error), Is.False);
            Assert.That(error, Does.Contain("must be finite"));
        }

        // Before every predicted tick the chunks around the predicted position must be loaded:
        // that loads what a refresh there would load, unloads nothing, and allocates nothing when
        // they are loaded already.
        [Test]
        public void EnsuringTheChunksAroundAnAnchorLoadsThemAndAllocatesNothingWhenLoaded()
        {
            Assert.That(
                UnityWorldCollisionLoader.TryCreateStream(RealtimeHubMessages.WorldId, out var stream, out var error),
                Is.True,
                error);
            var origin = new[] { new SimulationVector3(0f, 0f, 0f) };
            var far = new[] { new SimulationVector3(5000f, 0f, 5000f) };

            Assert.That(stream.TryEnsureLoaded(origin, out error), Is.True, error);
            var loaded = stream.CollisionWorld.ChunkCount;
            Assert.That(loaded, Is.GreaterThan(0));
            Assert.That(stream.Revision, Is.EqualTo(loaded));

            var ensured = true;
            Assert.That(ManagedAllocations.Of(() => ensured &= stream.TryEnsureLoaded(origin, out _)), Is.Zero);
            Assert.That(ensured, Is.True);
            Assert.That(stream.Revision, Is.EqualTo(loaded));

            // Far away nothing unloads, and a refresh at the start finds nothing to load.
            Assert.That(stream.TryEnsureLoaded(far, out error), Is.True, error);
            Assert.That(stream.CollisionWorld.ChunkCount, Is.EqualTo(loaded));
            Assert.That(stream.TryRefresh(origin, out error), Is.True, error);
            Assert.That(stream.Revision, Is.EqualTo(loaded));

            Assert.That(stream.TryEnsureLoaded(new[] { new SimulationVector3(0f, float.PositiveInfinity, 0f) }, out error), Is.False);
            Assert.That(error, Does.Contain("must be finite"));
        }
    }
}
