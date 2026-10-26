using System.Text.Json;
using Mmorpg.GameSimulation;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Shared.Tests;

// Follows planned routes with the shared movement simulation, and loads the collision of
// checked-in Worlds as the server and the client do: the compiled chunks, which hold the
// scene's shapes and the resource nodes that block movement.
internal static class MovementRouteAssertions
{
    public static PlayerMovementState AssertFollowsToTheEnd(
        ICollisionWorld world,
        MovementSimulationSettings settings,
        SimulationVector3 start,
        MovementPathPlan plan)
    {
        return AssertFollowsToTheEnd(world, settings, start, plan.Waypoints);
    }

    public static PlayerMovementState AssertFollowsToTheEnd(
        ICollisionWorld world,
        MovementSimulationSettings settings,
        SimulationVector3 start,
        IReadOnlyList<SimulationVector3> waypoints)
    {
        var path = new MovementPath(1, waypoints.ToArray());
        Assert.True(MovementPathRules.IsValid(path, start, settings, out var error), error);
        var state = PlayerMovementSimulation.CreateInitialState(settings, world, start.X, start.Y, start.Z, 0f);
        var maximumBlockedTicks = 0;
        for (uint tick = 1; tick <= 3_000 && state.PathProgress.NextWaypointIndex < path.Count; tick++)
        {
            state = PlayerMovementSimulation.Step(
                state,
                new PlayerMovementInput(tick, tick, path),
                settings,
                PlayerCarryState.Default,
                world);
            maximumBlockedTicks = Math.Max(maximumBlockedTicks, state.PathProgress.BlockedTicks);
        }

        Assert.Equal(path.Count, state.PathProgress.NextWaypointIndex);
        Assert.True(maximumBlockedTicks <= 1, $"The path was blocked for {maximumBlockedTicks} ticks.");
        var deltaX = state.PositionX - path.Destination.X;
        var deltaZ = state.PositionZ - path.Destination.Z;
        Assert.True(
            (deltaX * deltaX) + (deltaZ * deltaZ) < 0.0004f,
            $"Ended at ({state.PositionX}, {state.PositionY}, {state.PositionZ}).");
        return state;
    }

    public static bool IsOpenAbove(ICollisionWorld world, PlayerMovementState state, float height)
    {
        return !CollisionRaycast.TryRaycast(
            world,
            new SimulationVector3(state.PositionX, state.PositionY + 1f, state.PositionZ),
            new SimulationVector3(0f, 1f, 0f),
            height - state.PositionY - 1f,
            CollisionLayers.CharacterMovement,
            new CollisionQueryBuffer(),
            out _);
    }

    public static float DistanceXz(PlayerMovementState state, float x, float z)
    {
        var deltaX = state.PositionX - x;
        var deltaZ = state.PositionZ - z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    public static float PathLength(SimulationVector3 start, MovementPathPlan plan)
    {
        var length = 0f;
        var previous = start;
        foreach (var waypoint in plan.Waypoints)
        {
            var deltaX = waypoint.X - previous.X;
            var deltaZ = waypoint.Z - previous.Z;
            length += MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
            previous = waypoint;
        }

        return length;
    }

    public static ChunkedStaticCollisionWorld LoadCheckedInWorld(string worldId)
    {
        var directory = ContentLayout.GeneratedCollisionDirectory(RepositoryPaths.Generated, worldId);
        var manifest = JsonSerializer.Deserialize<CollisionWorldManifest>(
            File.ReadAllText(Path.Combine(directory, ContentLayout.CollisionManifestFileName)),
            new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true })!;
        return new ChunkedStaticCollisionWorld(
            manifest.WorldId,
            manifest.Revision,
            manifest.ChunkSize,
            manifest.Chunks.Select(entry =>
            {
                var bytes = File.ReadAllBytes(
                    Path.Combine(directory, entry.ResourceName + CollisionDataFormat.ChunkFileExtension));
                Assert.Equal(entry.Sha256, CollisionWorldCompiler.ComputeSha256Hex(bytes), ignoreCase: true);
                return CollisionChunkCodec.Decode(bytes);
            }));
    }

    // The scene's own collision as the Unity bake writes it, without the actors.
    public static ChunkedStaticCollisionWorld LoadSceneCollision(string worldId)
    {
        var authoring = JsonSerializer.Deserialize<CollisionWorldAuthoringDocument>(
            File.ReadAllText(ContentLayout.CollisionSourcePath(RepositoryPaths.Content, worldId)),
            new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true })!;
        return ToWorld(CollisionWorldCompiler.Compile(authoring));
    }

    public static ChunkedStaticCollisionWorld ToWorld(CollisionWorldBakeResult bake)
    {
        return new ChunkedStaticCollisionWorld(
            bake.Manifest.WorldId,
            bake.Manifest.Revision,
            bake.Manifest.ChunkSize,
            bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
    }
}
