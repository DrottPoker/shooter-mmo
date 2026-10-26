using System;
using System.Linq;

namespace Mmorpg.WorldData.Actors
{
    // One box that blocks character movement at a placed actor, in World coordinates.
    public readonly struct WorldActorCollisionBox
    {
        public WorldActorCollisionBox(
            string sourcePath,
            float centerX,
            float centerY,
            float centerZ,
            float halfExtentX,
            float halfExtentY,
            float halfExtentZ)
        {
            SourcePath = sourcePath;
            CenterX = centerX;
            CenterY = centerY;
            CenterZ = centerZ;
            HalfExtentX = halfExtentX;
            HalfExtentY = halfExtentY;
            HalfExtentZ = halfExtentZ;
        }

        public string SourcePath { get; }
        public float CenterX { get; }
        public float CenterY { get; }
        public float CenterZ { get; }
        public float HalfExtentX { get; }
        public float HalfExtentY { get; }
        public float HalfExtentZ { get; }
    }

    // The static movement collision of a World's actors: one box per resource node instance, its
    // definition's collision bounds offset from the instance position like the interaction
    // bounds and aligned to the World axes whatever the yaw. Resource nodes never move and keep
    // their footprint when depleted, so the World collision compilers bake these boxes into the
    // World's collision chunks with the scene's, and the server and every client collide alike.
    // Every value is a sum or half of floats, which .NET and Mono round alike.
    public static class WorldActorCollision
    {
        public const string SourcePathPrefix = "Actors/";

        public static WorldActorCollisionBox[] Boxes(WorldActorRuntimeDocument world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            var actors = world.Actors.ToDictionary(actor => actor.Id, StringComparer.Ordinal);
            return world.SpawnInstances
                .Where(instance => actors.TryGetValue(instance.ActorId, out var actor)
                    && string.Equals(actor.Kind, WorldActorKindIds.ResourceNode, StringComparison.Ordinal))
                .OrderBy(instance => instance.Id, StringComparer.Ordinal)
                .Select(instance =>
                {
                    var bounds = actors[instance.ActorId].CollisionBounds;
                    return new WorldActorCollisionBox(
                        SourcePathPrefix + instance.Id,
                        (float)(instance.X + bounds.CenterX),
                        (float)(instance.Y + bounds.CenterY),
                        (float)(instance.Z + bounds.CenterZ),
                        (float)(bounds.SizeX * 0.5f),
                        (float)(bounds.SizeY * 0.5f),
                        (float)(bounds.SizeZ * 0.5f));
                })
                .ToArray();
        }
    }
}
