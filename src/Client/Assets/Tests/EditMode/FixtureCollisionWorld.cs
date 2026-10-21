using System.Linq;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Tests.EditMode
{
    // A small test-owned movement map compiled in memory, so movement and navigation tests
    // never depend on the layout of a development World: a 30 by 30 meter ground, a wall
    // north of the start, low and high cover, and a 12 degree ramp south of the start.
    internal static class FixtureCollisionWorld
    {
        public const string WorldId = "development-world-1";

        public static ChunkedStaticCollisionWorld Create()
        {
            var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = WorldId,
                ChunkSize = 32f,
                Boxes = new[]
                {
                    Box("Ground", 0f, -0.25f, 0f, 15f, 0.25f, 15f),
                    Box("Wall", 0f, 1.5f, 4f, 4f, 1.5f, 0.25f),
                    Box("LowCover", -4f, 0.75f, -2f, 2f, 0.75f, 0.5f),
                    Box("HighCover", 5f, 1.5f, 1f, 1f, 1.5f, 1f),
                    Box("Ramp", 0f, 0.4f, -7f, 2.5f, 0.25f, 4f, 12f)
                }
            });
            return new ChunkedStaticCollisionWorld(
                bake.Manifest.WorldId,
                bake.Manifest.Revision,
                bake.Manifest.ChunkSize,
                bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
        }

        private static CollisionBoxAuthoringEntry Box(
            string name,
            float centerX,
            float centerY,
            float centerZ,
            float halfExtentX,
            float halfExtentY,
            float halfExtentZ,
            float pitchDegrees = 0f)
        {
            var halfAngle = pitchDegrees * 0.5f * (float)System.Math.PI / 180f;
            return new CollisionBoxAuthoringEntry
            {
                SourcePath = "Fixture/" + name + "#BoxCollider0",
                LayerMask = CollisionLayers.CharacterMovement | CollisionLayers.CombatQueries,
                CenterX = centerX,
                CenterY = centerY,
                CenterZ = centerZ,
                HalfExtentX = halfExtentX,
                HalfExtentY = halfExtentY,
                HalfExtentZ = halfExtentZ,
                RotationX = (float)System.Math.Sin(halfAngle),
                RotationW = (float)System.Math.Cos(halfAngle)
            };
        }
    }
}
