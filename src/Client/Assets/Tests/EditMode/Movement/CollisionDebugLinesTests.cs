using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The Debug menu draws the baked collision near the player: triangle edges by slope, box
    // edges by the layers a box blocks, and nothing beyond its reach.
    public sealed class CollisionDebugLinesTests
    {
        [Test]
        public void ShapesNearTheCenterBecomeEdgesOfTheirKind()
        {
            var world = CreateWorld();
            var lines = new CollisionDebugLines();

            lines.Build(world, Vector3.zero, 24f, 50f);

            Assert.That(lines.ShapeCount, Is.EqualTo(5));
            Assert.That(lines.Segments(CollisionDebugKind.WalkableGround).Count, Is.EqualTo(6));
            Assert.That(lines.Segments(CollisionDebugKind.SteepSurface).Count, Is.EqualTo(6));
            Assert.That(lines.Segments(CollisionDebugKind.Obstacle).Count, Is.EqualTo(24));
            Assert.That(lines.Segments(CollisionDebugKind.MovementOnly).Count, Is.EqualTo(24));
            Assert.That(lines.Segments(CollisionDebugKind.CombatOnly).Count, Is.EqualTo(24));
            // Ground lines float above their triangle along its normal.
            Assert.That(
                lines.Segments(CollisionDebugKind.WalkableGround).Select(point => point.y),
                Has.All.EqualTo(CollisionDebugLines.TriangleLift).Within(0.0001f));
            // The twelve edges of the obstacle reach its eight corners and nothing else.
            var corners = lines.Segments(CollisionDebugKind.Obstacle).Distinct().ToArray();
            Assert.That(corners.Length, Is.EqualTo(8));
            Assert.That(corners.Select(corner => corner.x).Distinct(), Is.EquivalentTo(new[] { -6f, -4f }));
            Assert.That(corners.Select(corner => corner.y).Distinct(), Is.EquivalentTo(new[] { 0f, 1f }));
        }

        [Test]
        public void ARebuildForgetsTheShapesOfTheLastCenter()
        {
            var world = CreateWorld();
            var lines = new CollisionDebugLines();
            lines.Build(world, Vector3.zero, 24f, 50f);

            lines.Build(world, new Vector3(100f, 0f, 100f), 4f, 50f);

            Assert.That(lines.ShapeCount, Is.EqualTo(1));
            Assert.That(lines.Segments(CollisionDebugKind.Obstacle).Count, Is.EqualTo(24));
            Assert.That(lines.Segments(CollisionDebugKind.WalkableGround), Is.Empty);
            Assert.That(lines.Segments(CollisionDebugKind.MovementOnly), Is.Empty);

            lines.Build(null, Vector3.zero, 24f, 50f);
            Assert.That(lines.ShapeCount, Is.Zero);
            Assert.That(lines.Segments(CollisionDebugKind.Obstacle), Is.Empty);
        }

        // A flat and an upright triangle, an obstacle, a movement-only and a sight-only box near
        // the origin, and another obstacle far away.
        private static ChunkedStaticCollisionWorld CreateWorld()
        {
            var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = "collision-debug-lines",
                ChunkSize = 32f,
                Boxes = new[]
                {
                    Box("Obstacle", CollisionLayers.CharacterMovement | CollisionLayers.CombatQueries, -5f, 0f),
                    CollisionBoxAuthoringEntry.MovementObstacle("Fixture/Node#BoxCollider0", 5f, 0.5f, 0f, 1f, 0.5f, 1f),
                    Box("SightOnly", CollisionLayers.CombatQueries, 0f, -5f),
                    Box("Far", CollisionLayers.CharacterMovement | CollisionLayers.CombatQueries, 100f, 100f)
                },
                Meshes = new[]
                {
                    new CollisionMeshAuthoringEntry
                    {
                        SourcePath = "Fixture/Terrain#MeshCollider0",
                        LayerMask = CollisionLayers.CharacterMovement | CollisionLayers.CombatQueries,
                        Vertices = CollisionAuthoringEncoding.EncodeFloats(new[]
                        {
                            0f, 0f, 0f, 0f, 0f, 1f, 1f, 0f, 0f,
                            3f, 0f, 3f, 3f, 2f, 3f, 4f, 0f, 3f
                        }),
                        Triangles = CollisionAuthoringEncoding.EncodeInts(new[] { 0, 1, 2, 3, 4, 5 })
                    }
                }
            });
            return new ChunkedStaticCollisionWorld(
                bake.Manifest.WorldId,
                bake.Manifest.Revision,
                bake.Manifest.ChunkSize,
                bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
        }

        private static CollisionBoxAuthoringEntry Box(string name, uint layers, float x, float z)
        {
            return new CollisionBoxAuthoringEntry
            {
                SourcePath = "Fixture/" + name + "#BoxCollider0",
                LayerMask = layers,
                CenterX = x,
                CenterY = 0.5f,
                CenterZ = z,
                HalfExtentX = 1f,
                HalfExtentY = 0.5f,
                HalfExtentZ = 1f,
                RotationW = 1f
            };
        }
    }
}
