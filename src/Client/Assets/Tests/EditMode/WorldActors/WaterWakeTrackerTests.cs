using System.Linq;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A body in water leaves a ring now and then, often while it moves, and spray in
    // proportion to the distance it wades; a body on dry ground or in a film of water leaves
    // nothing. The water is a 20 by 20 meter pond whose surface lies at 0 m over a floor 3 m
    // down.
    public sealed class WaterWakeTrackerTests
    {
        private const float Frame = 0.02f;

        private static ChunkedStaticCollisionWorld pond;

        private static ICollisionWorld Pond
        {
            get
            {
                if (pond == null)
                {
                    var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
                    {
                        FormatVersion = CollisionDataFormat.Version,
                        WorldId = "wake-tests",
                        ChunkSize = 32f,
                        Boxes = new[]
                        {
                            new CollisionBoxAuthoringEntry
                            {
                                SourcePath = "Test/Floor#BoxCollider0",
                                LayerMask = CollisionLayers.CharacterMovement,
                                CenterY = -3.25f,
                                HalfExtentX = 10f,
                                HalfExtentY = 0.25f,
                                HalfExtentZ = 10f,
                                RotationW = 1f
                            }
                        },
                        Meshes = new CollisionMeshAuthoringEntry[0],
                        Heightfields = new CollisionHeightfieldAuthoringEntry[0],
                        Water = new[]
                        {
                            new CollisionWaterAuthoringEntry
                            {
                                SourcePath = "Test/Pond#WaterBody",
                                BodyId = "test-pond",
                                Vertices = CollisionAuthoringEncoding.EncodeFloats(new[]
                                {
                                    -10f, 0f, -10f,
                                    -10f, 0f, 10f,
                                    10f, 0f, 10f,
                                    10f, 0f, -10f
                                }),
                                Triangles = CollisionAuthoringEncoding.EncodeInts(new[] { 0, 1, 2, 0, 2, 3 })
                            }
                        }
                    });
                    pond = new ChunkedStaticCollisionWorld(
                        bake.Manifest.WorldId,
                        bake.Manifest.Revision,
                        bake.Manifest.ChunkSize,
                        bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
                }

                return pond;
            }
        }

        [Test]
        public void ABodyOutOfTheWaterOrInAFilmOfItLeavesNoWake()
        {
            var tracker = new WaterWakeTracker();
            var body = new object();

            tracker.BeginFrame();
            Assert.That(tracker.Visit(Pond, body, new Vector3(0f, 0.1f, 0f), Frame, out _), Is.False);
            Assert.That(tracker.Visit(Pond, new object(), new Vector3(15f, -0.6f, 0f), Frame, out _), Is.False);
            var film = WaterWakeTracker.MinimumDepth * 0.5f;
            Assert.That(tracker.Visit(Pond, new object(), new Vector3(0f, -film, 0f), Frame, out _), Is.False);
            tracker.EndFrame();

            Assert.That(tracker.WadingCount, Is.Zero);
        }

        [Test]
        public void AStillBodyLeavesASmallRingNowAndThenAndNoSpray()
        {
            var tracker = new WaterWakeTracker();
            var body = new object();
            var feet = new Vector3(2f, -0.6f, 3f);

            var wake = Step(tracker, body, feet);
            Assert.That(tracker.WadingCount, Is.EqualTo(1));
            Assert.That(wake.RingSize, Is.EqualTo(WaterWakeTracker.StillRingSize));
            Assert.That(wake.Spray, Is.Zero);
            Assert.That(wake.Surface.y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(wake.BodyId, Is.EqualTo("test-pond"));

            // The next ring comes once the interval has passed, one frame later at most.
            var rings = 0;
            var frames = Mathf.RoundToInt(WaterWakeTracker.StillRingSeconds / Frame) + 1;
            for (var frame = 0; frame < frames; frame++)
            {
                rings += Step(tracker, body, feet).RingSize > 0f ? 1 : 0;
            }

            Assert.That(rings, Is.EqualTo(1));
        }

        [Test]
        public void AWadingBodyLeavesRingsOftenAndSprayForTheDistance()
        {
            var tracker = new WaterWakeTracker();
            var body = new object();
            const float speed = 5f;
            Step(tracker, body, new Vector3(0f, -0.6f, 0f));

            var rings = 0;
            var spray = 0;
            var frames = Mathf.RoundToInt(1f / Frame);
            for (var frame = 1; frame <= frames; frame++)
            {
                var wake = Step(tracker, body, new Vector3(speed * Frame * frame, -0.6f, 0f));
                rings += wake.RingSize > 0f ? 1 : 0;
                spray += wake.Spray;
                Assert.That(wake.Heading.x, Is.EqualTo(1f).Within(0.001f));
                Assert.That(wake.Speed, Is.EqualTo(speed).Within(0.01f));
            }

            Assert.That(rings, Is.InRange(4, 5));
            Assert.That(spray, Is.InRange(44, 45));
        }

        [Test]
        public void ABodyThatLeftAndCameBackStartsStill()
        {
            var tracker = new WaterWakeTracker();
            var body = new object();
            Step(tracker, body, new Vector3(0f, -0.6f, 0f));

            tracker.BeginFrame();
            tracker.EndFrame();

            var wake = Step(tracker, body, new Vector3(1f, -0.6f, 0f));
            Assert.That(wake.RingSize, Is.EqualTo(WaterWakeTracker.StillRingSize));
            Assert.That(wake.Spray, Is.Zero);

            var placed = Step(tracker, body, new Vector3(1f + WaterWakeTracker.TeleportDistance, -0.6f, 0f));
            Assert.That(placed.Spray, Is.Zero);
            Assert.That(placed.Speed, Is.Zero);
        }

        // A crowd that stands in or beside the water asks the collision world nothing: a body
        // that stands exactly where it stood keeps its depth, on the same revision of the same
        // world, and still leaves its rings on time. A body that moves, or new chunks, ask again.
        [Test]
        public void ABodyThatStandsStillAsksForItsDepthOnce()
        {
            var tracker = new WaterWakeTracker();
            var world = new CountingWorld(Pond);
            var wading = new object();
            var dry = new object();
            var feet = new Vector3(2f, -0.6f, 3f);
            var bank = new Vector3(15f, 0f, 0f);
            var frames = Mathf.RoundToInt(WaterWakeTracker.StillRingSeconds / Frame) + 1;
            var rings = 0;
            var firstQueries = 0;
            for (var frame = 0; frame <= frames; frame++)
            {
                tracker.BeginFrame();
                Assert.That(tracker.Visit(world, 0, wading, feet, Frame, out var wake), Is.True);
                Assert.That(tracker.Visit(world, 0, dry, bank, Frame, out _), Is.False);
                tracker.EndFrame();
                rings += wake.RingSize > 0f ? 1 : 0;
                Assert.That(wake.Surface.y, Is.EqualTo(0f).Within(0.001f));
                Assert.That(wake.BodyId, Is.EqualTo("test-pond"));
                if (frame == 0)
                {
                    firstQueries = world.Queries;
                }
            }

            Assert.That(firstQueries, Is.GreaterThan(0));
            Assert.That(world.Queries, Is.EqualTo(firstQueries), "standing bodies ask nothing after the first frame");
            Assert.That(rings, Is.EqualTo(2), "the first ring and the next one on time");

            // Ground that loaded or unloaded may hold other water.
            Step(tracker, world, 1, wading, feet);
            var reloaded = world.Queries;
            Assert.That(reloaded, Is.GreaterThan(firstQueries));
            Step(tracker, world, 1, wading, feet);
            Assert.That(world.Queries, Is.EqualTo(reloaded));

            // A body that moves asks in every frame.
            var asked = world.Queries;
            for (var frame = 1; frame <= 5; frame++)
            {
                Step(tracker, world, 1, wading, feet + new Vector3(0.05f * frame, 0f, 0f));
                Assert.That(world.Queries, Is.GreaterThan(asked));
                asked = world.Queries;
            }
        }

        private static WaterWake Step(WaterWakeTracker tracker, ICollisionWorld world, int revision, object body, Vector3 feet)
        {
            tracker.BeginFrame();
            Assert.That(tracker.Visit(world, revision, body, feet, Frame, out var wake), Is.True);
            tracker.EndFrame();
            return wake;
        }

        private static WaterWake Step(WaterWakeTracker tracker, object body, Vector3 feet)
        {
            tracker.BeginFrame();
            Assert.That(tracker.Visit(Pond, body, feet, Frame, out var wake), Is.True);
            tracker.EndFrame();
            return wake;
        }

        // The pond, counting how often it is asked for shapes.
        private sealed class CountingWorld : ICollisionWorld
        {
            private readonly ICollisionWorld world;

            public CountingWorld(ICollisionWorld world)
            {
                this.world = world;
            }

            public int Queries { get; private set; }

            public void QueryShapes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
            {
                Queries++;
                world.QueryShapes(bounds, layerMask, buffer);
            }
        }
    }
}
