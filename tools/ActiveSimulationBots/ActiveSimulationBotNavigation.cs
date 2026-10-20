using System.Collections.Concurrent;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Worlds;

namespace Mmorpg.Tools.ActiveSimulationBots;

// Plans bot walks with the shared planner on the World's baked collision, as the Unity client
// plans click-to-move paths, so bots walk around buildings instead of into them. Plans run on
// background threads, each with its own planner, because one search can take milliseconds and
// the poll loop must send every bot's inputs on time.
public sealed class ActiveSimulationBotNavigation : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly BlockingCollection<PlanRequest> requests;
    private readonly Thread[] threads;

    private ActiveSimulationBotNavigation(ChunkedStaticCollisionWorld collisionWorld, int threadCount)
    {
        CollisionWorld = collisionWorld;
        requests = new BlockingCollection<PlanRequest>(threadCount * 64);
        threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            threads[index] = new Thread(PlanRequests)
            {
                IsBackground = true,
                Name = $"ActiveSimulationBots planner {index + 1}"
            };
            threads[index].Start();
        }
    }

    public ChunkedStaticCollisionWorld CollisionWorld { get; }

    // Loads every collision chunk of the World from the tool's output, where the build copies
    // the same compiled collision the worker uses, and checks it like the worker does.
    public static ActiveSimulationBotNavigation Load(string worldDataPath, string worldId, int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        var directory = WorldManifestFileStore.ResolveCollisionRuntimePath(worldDataPath, worldId);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<CollisionWorldManifest>(
                File.ReadAllText(manifestPath),
                ManifestJsonOptions)
            ?? throw new InvalidDataException($"Collision manifest '{manifestPath}' is empty.");
        if (manifest.FormatVersion != CollisionDataFormat.Version
            || !string.Equals(manifest.WorldId, worldId, StringComparison.OrdinalIgnoreCase)
            || manifest.Chunks is null
            || !string.Equals(
                CollisionWorldCompiler.ComputeManifestRevision(manifest),
                manifest.Revision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Collision manifest '{manifestPath}' is invalid.");
        }

        var collisionWorld = new ChunkedStaticCollisionWorld(
            manifest.WorldId,
            manifest.Revision,
            manifest.ChunkSize);
        foreach (var entry in manifest.Chunks)
        {
            var bytes = File.ReadAllBytes(
                Path.Combine(directory, entry.ResourceName + CollisionDataFormat.ChunkFileExtension));
            if (!string.Equals(
                    CollisionWorldCompiler.ComputeSha256Hex(bytes),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                || !collisionWorld.TryLoadChunk(CollisionChunkCodec.Decode(bytes)))
            {
                throw new InvalidDataException($"Collision chunk '{entry.ResourceName}' is invalid.");
            }
        }

        return Create(collisionWorld, threadCount);
    }

    public static ActiveSimulationBotNavigation Create(
        ChunkedStaticCollisionWorld collisionWorld,
        int threadCount)
    {
        ArgumentNullException.ThrowIfNull(collisionWorld);
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        return new ActiveSimulationBotNavigation(collisionWorld, threadCount);
    }

    // Queues a plan from start toward destination. Returns null when the planners are busy, so
    // the bot stands this time instead of delaying everyone's inputs.
    public Task<MovementPathPlan>? TryPlan(
        RealtimeMovementSettings movement,
        SimulationVector3 start,
        SimulationVector3 destination)
    {
        var completion = new TaskCompletionSource<MovementPathPlan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return requests.TryAdd(new PlanRequest(movement, start, destination, completion))
            ? completion.Task
            : null;
    }

    public void Dispose()
    {
        requests.CompleteAdding();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        requests.Dispose();
    }

    private void PlanRequests()
    {
        RealtimeMovementSettings? plannerSettings = null;
        MovementPathPlanner? planner = null;
        foreach (var request in requests.GetConsumingEnumerable())
        {
            try
            {
                // Every bot of one worker shares the same settings, so a thread keeps its
                // planner and the buffers it grew.
                if (planner is null || !SameMovement(plannerSettings!, request.Movement))
                {
                    plannerSettings = request.Movement;
                    planner = new MovementPathPlanner(
                        CollisionWorld,
                        ToSimulationSettings(request.Movement),
                        MovementPathPlannerSettings.Default);
                }

                request.Completion.TrySetResult(planner.Plan(request.Start, request.Destination));
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    // The same mapping the Unity client uses for its prediction and planner.
    public static MovementSimulationSettings ToSimulationSettings(RealtimeMovementSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MovementSimulationSettings(
            source.TickRateHz,
            source.WalkSpeed,
            source.RotationSpeedDegrees,
            source.Gravity,
            source.MaximumFallSpeed,
            source.GroundedVerticalVelocity,
            source.GroundHeight,
            source.MinimumX,
            source.MaximumX,
            source.MinimumZ,
            source.MaximumZ,
            new CharacterCollisionSettings(
                source.CharacterRadius,
                source.CharacterHeight,
                source.StepHeight,
                source.MaximumSlopeDegrees,
                source.GroundSnapDistance,
                source.MaximumSubstepDistance,
                source.MaximumPenetrationIterations));
    }

    private static bool SameMovement(RealtimeMovementSettings left, RealtimeMovementSettings right)
    {
        return ReferenceEquals(left, right)
            || (left.TickRateHz == right.TickRateHz
                && left.WalkSpeed == right.WalkSpeed
                && left.RotationSpeedDegrees == right.RotationSpeedDegrees
                && left.Gravity == right.Gravity
                && left.MaximumFallSpeed == right.MaximumFallSpeed
                && left.GroundedVerticalVelocity == right.GroundedVerticalVelocity
                && left.GroundHeight == right.GroundHeight
                && left.MinimumX == right.MinimumX
                && left.MaximumX == right.MaximumX
                && left.MinimumZ == right.MinimumZ
                && left.MaximumZ == right.MaximumZ
                && left.CharacterRadius == right.CharacterRadius
                && left.CharacterHeight == right.CharacterHeight
                && left.StepHeight == right.StepHeight
                && left.MaximumSlopeDegrees == right.MaximumSlopeDegrees
                && left.GroundSnapDistance == right.GroundSnapDistance
                && left.MaximumSubstepDistance == right.MaximumSubstepDistance
                && left.MaximumPenetrationIterations == right.MaximumPenetrationIterations);
    }

    private sealed record PlanRequest(
        RealtimeMovementSettings Movement,
        SimulationVector3 Start,
        SimulationVector3 Destination,
        TaskCompletionSource<MovementPathPlan> Completion);
}
