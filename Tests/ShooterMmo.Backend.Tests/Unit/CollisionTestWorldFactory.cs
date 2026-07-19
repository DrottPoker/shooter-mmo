using ShooterMmo.GameSimulation;

namespace ShooterMmo.Backend.Tests.Unit;

internal static class CollisionTestWorldFactory
{
    public static ChunkedStaticCollisionWorld Create()
    {
        var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
        {
            FormatVersion = CollisionDataFormat.Version,
            WorldId = "development-world-1",
            ChunkSize = 32f,
            Boxes =
            [
                new CollisionBoxAuthoringEntry
                {
                    SourcePath = "Test/Ground#BoxCollider0",
                    LayerMask = CollisionLayers.CharacterMovement,
                    CenterY = -0.25f,
                    HalfExtentX = 15f,
                    HalfExtentY = 0.25f,
                    HalfExtentZ = 15f,
                    RotationW = 1f
                }
            ]
        });
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }
}
