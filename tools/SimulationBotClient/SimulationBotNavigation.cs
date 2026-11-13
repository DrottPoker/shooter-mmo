using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Worlds;

namespace Mmorpg.Tools.SimulationBotClient;

// Plans bot walks with the shared planner on the World's baked collision, as the Unity client
// plans click-to-move paths, so bots walk around buildings instead of into them. Plans run on
// background threads, each with its own planner, because one search can take milliseconds and
// the poll loop must send every bot's inputs on time. Both bot tools use this one copy.
public sealed class SimulationBotNavigation : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly BlockingCollection<PlanRequest> requests;
    private readonly Thread[] threads;
    private readonly Action<SimulationBotPlanTiming>? planned;

    private SimulationBotNavigation(
        ChunkedStaticCollisionWorld collisionWorld,
        int threadCount,
        Action<SimulationBotPlanTiming>? planned)
    {
        CollisionWorld = collisionWorld;
        this.planned = planned;
        requests = new BlockingCollection<PlanRequest>(threadCount * 64);
        threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            threads[index] = new Thread(PlanRequests)
            {
                IsBackground = true,
                Name = $"Simulation bot planner {index + 1}"
            };
            threads[index].Start();
        }
    }

    public ChunkedStaticCollisionWorld CollisionWorld { get; }

    // Loads every collision chunk of the World from a World data folder laid out as the
    // worker's, <root>/<worldId>/Collision, and checks it like the worker does. Planned is
    // called on a planner thread with the queue wait and the search time of every plan.
    public static SimulationBotNavigation Load(
        string worldDataPath,
        string worldId,
        int threadCount,
        Action<SimulationBotPlanTiming>? planned = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        return Create(LoadCollisionWorld(worldDataPath, worldId), threadCount, planned);
    }

    public static ChunkedStaticCollisionWorld LoadCollisionWorld(string worldDataPath, string worldId)
    {
        return LoadCollisionDirectory(
            WorldManifestFileStore.ResolveCollisionRuntimePath(worldDataPath, worldId),
            worldId);
    }

    // Loads the compiled collision of one World from the folder that holds its manifest.
    public static ChunkedStaticCollisionWorld LoadCollisionDirectory(string directory, string worldId)
    {
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

        return collisionWorld;
    }

    public static SimulationBotNavigation Create(
        ChunkedStaticCollisionWorld collisionWorld,
        int threadCount,
        Action<SimulationBotPlanTiming>? planned = null)
    {
        ArgumentNullException.ThrowIfNull(collisionWorld);
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        return new SimulationBotNavigation(collisionWorld, threadCount, planned);
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
        return requests.TryAdd(new PlanRequest(
                movement,
                start,
                destination,
                completion,
                Stopwatch.GetTimestamp()))
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
            var started = Stopwatch.GetTimestamp();
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

                var plan = planner.Plan(request.Start, request.Destination);
                planned?.Invoke(new SimulationBotPlanTiming(
                    Stopwatch.GetElapsedTime(request.QueuedTimestamp, started),
                    Stopwatch.GetElapsedTime(started),
                    plan.Status));
                request.Completion.TrySetResult(plan);
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
        TaskCompletionSource<MovementPathPlan> Completion,
        long QueuedTimestamp);
}

// How long one plan waited for a planner thread and how long its search took.
public readonly record struct SimulationBotPlanTiming(
    TimeSpan QueueWait,
    TimeSpan SearchTime,
    MovementPathPlanStatus Status);
