#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.GameSimulation;

namespace Mmorpg.SharedTests
{
    // Compiled into both the Unity EditMode tests and the .NET backend tests, so the
    // client and server runtimes run the exact same movement scenario.
    public static class MovementDeterminismScenario
    {
        // Change both values together, and only for an intended change to shared movement
        // or collision behavior: bump GameSimulationCompatibility.Revision and pin the new
        // fingerprint. Clients and workers with different revisions must not interoperate.
        public const string ExpectedSimulationRevision = "movement-simulation-v5";
        public const string ExpectedFingerprint = "3ba403655f002a79";

        // The planner does not affect the wire contract, because paths travel as data, so
        // its fingerprint changes without a revision bump. It still must match in both
        // runtimes, so the worker can plan for world actors with the same results.
        public const string ExpectedPlannerFingerprint = "98c0afe164955178";

        private const int TickCount = 900;

        private static readonly PlayerCarryState Encumbered = new PlayerCarryState(1, 240, 200);

        // Each click steers through one collision case: blocked by a wall until the path
        // gives up, sliding along it, climbing a rotated ramp and dropping off its high
        // edge, blocked by a rotated pillar, stepping up onto a step while encumbered, and
        // a click that replaces a path halfway.
        private static readonly Click[] Route =
        {
            new Click(1, 60, false, new[] { Point(10f, 0f) }),
            new Click(2, 120, false, new[] { Point(6f, 6f), Point(0f, 16f) }),
            new Click(3, 90, false, new[] { Point(0f, 3f) }),
            new Click(4, 90, false, new[] { Point(2f, -8f) }),
            new Click(5, 120, true, new[] { Point(-6f, 0f), Point(-6f, 0.5f), Point(-12f, 0f) }),
            new Click(6, 20, false, new[] { Point(0f, -2f) }),
            new Click(7, 60, false, new[] { Point(-3f, -3f), Point(3f, 3f) }),
            new Click(0, 30, false, null)
        };

        public static string ComputeFingerprint()
        {
            return ComputeTrace().Fingerprint;
        }

        // The per-tick states let a failing runtime be compared tick by tick.
        public static MovementDeterminismTrace ComputeTrace()
        {
            var collisionWorld = CreateCollisionWorld();
            var settings = CreateSettings();
            var queryBuffer = new CollisionQueryBuffer();
            var state = PlayerMovementSimulation.CreateInitialState(
                settings,
                collisionWorld,
                0f,
                0f,
                0f,
                0f);
            var states = new List<PlayerMovementState>(TickCount);
            var hash = 14695981039346656037UL;
            var clickIndex = 0;
            var clickTick = 0;
            var nextPathId = (uint)Route.Length;
            for (uint tick = 1; tick <= TickCount; tick++)
            {
                MovementPath path;
                var carryState = PlayerCarryState.Default;
                if (clickIndex < Route.Length)
                {
                    var click = Route[clickIndex];
                    path = click.Path;
                    carryState = click.Encumbered ? Encumbered : PlayerCarryState.Default;
                    clickTick++;
                    if (clickTick >= click.TickBudget)
                    {
                        clickIndex++;
                        clickTick = 0;
                    }
                }
                else
                {
                    // After the route: a new click toward alternating corners every 40 ticks.
                    var phase = (int)((tick / 40) % 4);
                    var corner = phase == 0 ? Point(-4f, 4f)
                        : phase == 1 ? Point(4f, -4f)
                        : phase == 2 ? Point(-2f, -5f)
                        : Point(3f, 5f);
                    var pathId = nextPathId + (tick / 40);
                    path = new MovementPath(pathId, new[] { corner });
                }

                state = PlayerMovementSimulation.Step(
                    state,
                    new PlayerMovementInput(tick, tick, path),
                    settings,
                    carryState,
                    collisionWorld,
                    queryBuffer);
                states.Add(state);
                hash = Mix(hash, state);
            }

            return new MovementDeterminismTrace(
                hash.ToString("x16", CultureInfo.InvariantCulture),
                states);
        }

        // Plans around the wall and up the ramp, and hashes every waypoint.
        public static MovementPlannerTrace ComputePlannerTrace()
        {
            var collisionWorld = CreateCollisionWorld();
            var planner = new MovementPathPlanner(
                collisionWorld,
                CreateSettings(),
                MovementPathPlannerSettings.Default);
            var plans = new[]
            {
                planner.Plan(new SimulationVector3(9f, 0f, 0f), new SimulationVector3(4f, 0f, 0.5f)),
                planner.Plan(new SimulationVector3(0f, 0f, 18f), new SimulationVector3(0f, 2f, 7f)),
                planner.Plan(new SimulationVector3(-12f, 0f, -12f), new SimulationVector3(-6f, 0.3f, 1f))
            };
            var hash = 14695981039346656037UL;
            foreach (var plan in plans)
            {
                hash = Mix(hash, (uint)plan.Status);
                hash = Mix(hash, (uint)plan.Waypoints.Count);
                foreach (var waypoint in plan.Waypoints)
                {
                    hash = Mix(hash, waypoint.X);
                    hash = Mix(hash, waypoint.Y);
                    hash = Mix(hash, waypoint.Z);
                }
            }

            return new MovementPlannerTrace(
                hash.ToString("x16", CultureInfo.InvariantCulture),
                plans,
                collisionWorld,
                CreateSettings());
        }

        public static MovementSimulationSettings CreateSettings()
        {
            return new MovementSimulationSettings(
                30,
                5f,
                720f,
                -24f,
                -2f,
                0f,
                -30f,
                30f,
                -30f,
                30f);
        }

        private static SimulationVector3 Point(float x, float z)
        {
            return new SimulationVector3(x, 0f, z);
        }

        private static ulong Mix(ulong hash, PlayerMovementState state)
        {
            hash = Mix(hash, state.PositionX);
            hash = Mix(hash, state.PositionY);
            hash = Mix(hash, state.PositionZ);
            hash = Mix(hash, state.VelocityX);
            hash = Mix(hash, state.VelocityY);
            hash = Mix(hash, state.VelocityZ);
            hash = Mix(hash, state.YawDegrees);
            hash = Mix(hash, state.IsGrounded ? 1u : 0u);
            hash = Mix(hash, state.PathProgress.PathId);
            hash = Mix(hash, (uint)state.PathProgress.NextWaypointIndex);
            hash = Mix(hash, (uint)state.PathProgress.BlockedTicks);
            return hash;
        }

        private static ulong Mix(ulong hash, float value)
        {
            return Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        }

        private static ulong Mix(ulong hash, uint value)
        {
            const ulong prime = 1099511628211UL;
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (value >> shift) & 0xffu;
                hash *= prime;
            }

            return hash;
        }

        private static ChunkedStaticCollisionWorld CreateCollisionWorld()
        {
            var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = "movement-determinism",
                ChunkSize = 16f,
                Boxes = new[]
                {
                    Box("Ground", 0f, -0.25f, 0f, 30f, 0.25f, 30f, 0f, 0f, 0f, 1f),
                    Box("Wall", 6.5f, 1.5f, 0f, 0.5f, 1.5f, 4f, 0f, 0f, 0f, 1f),
                    // A ramp rotated by 20 degrees around X that rises toward -Z,
                    // written as exact literals.
                    Box("Ramp", 0f, 0.5f, 10f, 2f, 0.25f, 4f, 0.17364818f, 0f, 0f, 0.98480775f),
                    // A pillar rotated by 45 degrees around Y.
                    Box("Pillar", 2f, 1f, -8f, 0.5f, 1f, 0.5f, 0f, 0.38268343f, 0f, 0.9238795f),
                    Box("Step", -6f, 0.15f, 0f, 2f, 0.15f, 2f, 0f, 0f, 0f, 1f)
                }
            });
            return new ChunkedStaticCollisionWorld(
                bake.Manifest.WorldId,
                bake.Manifest.Revision,
                bake.Manifest.ChunkSize,
                bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)).ToArray());
        }

        private static CollisionBoxAuthoringEntry Box(
            string name,
            float centerX,
            float centerY,
            float centerZ,
            float halfExtentX,
            float halfExtentY,
            float halfExtentZ,
            float rotationX,
            float rotationY,
            float rotationZ,
            float rotationW)
        {
            return new CollisionBoxAuthoringEntry
            {
                SourcePath = "MovementDeterminism/" + name + "#BoxCollider0",
                LayerMask = CollisionLayers.CharacterMovement,
                CenterX = centerX,
                CenterY = centerY,
                CenterZ = centerZ,
                HalfExtentX = halfExtentX,
                HalfExtentY = halfExtentY,
                HalfExtentZ = halfExtentZ,
                RotationX = rotationX,
                RotationY = rotationY,
                RotationZ = rotationZ,
                RotationW = rotationW
            };
        }

        private sealed class Click
        {
            public Click(uint pathId, int tickBudget, bool encumbered, SimulationVector3[] waypoints)
            {
                Path = waypoints == null ? null : new MovementPath(pathId, waypoints);
                TickBudget = tickBudget;
                Encumbered = encumbered;
            }

            public MovementPath Path { get; }

            public int TickBudget { get; }

            public bool Encumbered { get; }
        }
    }

    public sealed class MovementDeterminismTrace
    {
        public MovementDeterminismTrace(
            string fingerprint,
            IReadOnlyList<PlayerMovementState> states)
        {
            Fingerprint = fingerprint;
            States = states;
        }

        public string Fingerprint { get; }

        public IReadOnlyList<PlayerMovementState> States { get; }
    }

    public sealed class MovementPlannerTrace
    {
        public MovementPlannerTrace(
            string fingerprint,
            IReadOnlyList<MovementPathPlan> plans,
            ICollisionWorld collisionWorld,
            MovementSimulationSettings settings)
        {
            Fingerprint = fingerprint;
            Plans = plans;
            CollisionWorld = collisionWorld;
            Settings = settings;
        }

        public string Fingerprint { get; }

        public IReadOnlyList<MovementPathPlan> Plans { get; }

        public ICollisionWorld CollisionWorld { get; }

        public MovementSimulationSettings Settings { get; }
    }
}
