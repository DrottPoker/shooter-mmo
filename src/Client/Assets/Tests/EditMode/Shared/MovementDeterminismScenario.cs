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
        public const string ExpectedSimulationRevision = "movement-simulation-v7";
        public const string ExpectedFingerprint = "7c513e9f60c61a2e";

        // The planner does not affect the wire contract, because paths travel as data, so
        // its fingerprint changes without a revision bump. It still must match in both
        // runtimes, so the worker can plan for world actors with the same results.
        public const string ExpectedPlannerFingerprint = "cebc6accf5bc34d2";

        // The pond in the bowl around (19, 19): its surface lies at 1.8 m, and its deep water
        // edge, where the depth reaches 1.2 m, at a radius of about 2.27 m.
        public const float PondCenter = 19f;
        public const float PondSurface = 1.8f;

        private const int TickCount = 1920;

        private static readonly PlayerCarryState Encumbered = new PlayerCarryState(1, 240, 200);

        // Each click steers through one collision case: blocked by a wall until the path
        // gives up, sliding along it, climbing a rotated ramp and dropping off its high
        // edge, blocked by a rotated pillar, stepping up onto a step while encumbered, a
        // click that replaces a path halfway, climbing over a heightfield mound, blocked by
        // the front of a one-sided mesh wall, walking through its back, and wading into a
        // pond until its deep water edge stops the walk.
        private static readonly Click[] Route =
        {
            new Click(1, 60, false, new[] { Point(10f, 0f) }),
            new Click(2, 120, false, new[] { Point(6f, 6f), Point(0f, 16f) }),
            new Click(3, 90, false, new[] { Point(0f, 3f) }),
            new Click(4, 90, false, new[] { Point(2f, -8f) }),
            new Click(5, 120, true, new[] { Point(-6f, 0f), Point(-6f, 0.5f), Point(-12f, 0f) }),
            new Click(6, 20, false, new[] { Point(0f, -2f) }),
            new Click(7, 60, false, new[] { Point(-3f, -3f), Point(3f, 3f) }),
            new Click(8, 180, false, new[] { Point(3f, -6f), Point(18f, -18f) }),
            new Click(9, 60, false, new[] { Point(18f, -26f) }),
            new Click(10, 320, false, new[] { Point(-12f, -2f), Point(-20f, -2f) }),
            new Click(11, 240, false, new[] { Point(-14f, 10f), Point(-20f, 10f), Point(-20f, 0f), Point(-10f, 0f) }),
            new Click(12, 320, false, new[] { Point(-4f, 18f), Point(8f, 19f), Point(PondCenter, PondCenter) }),
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

        // Plans around the wall, up the ramp, onto the step, over the mound, and around the
        // mesh wall, and hashes every waypoint.
        public static MovementPlannerTrace ComputePlannerTrace()
        {
            var collisionWorld = CreateCollisionWorld();
            var planner = new MovementPathPlanner(
                collisionWorld,
                CreateSettings(),
                MovementPathPlannerSettings.Default);
            var starts = new[]
            {
                new SimulationVector3(9f, 0f, 0f),
                new SimulationVector3(0f, 0f, 18f),
                new SimulationVector3(-12f, 0f, -12f),
                new SimulationVector3(10f, 0f, -12f),
                new SimulationVector3(-12f, 0f, 0f),
                new SimulationVector3(8f, 0f, PondCenter)
            };
            var destinations = new[]
            {
                new SimulationVector3(4f, 0f, 0.5f),
                new SimulationVector3(0f, 2f, 7f),
                new SimulationVector3(-6f, 0.3f, 1f),
                new SimulationVector3(18f, 3f, -18f),
                new SimulationVector3(-20f, 0f, 0f),
                new SimulationVector3(PondCenter, 0.05f, PondCenter)
            };
            var plans = new MovementPathPlan[starts.Length];
            for (var index = 0; index < starts.Length; index++)
            {
                plans[index] = planner.Plan(starts[index], destinations[index]);
            }

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
                starts,
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
                },
                Meshes = new[]
                {
                    // A wall at x = -16 whose front faces +X.
                    Mesh(
                        "EastFacingWall",
                        new[]
                        {
                            -16f, -0.1f, -8f,
                            -16f, 3f, -8f,
                            -16f, 3f, 8f,
                            -16f, -0.1f, 8f
                        },
                        new[] { 0, 1, 2, 0, 2, 3 })
                },
                Heightfields = new[] { Mound(), Bowl() },
                Water = new[] { Pond() }
            });
            return new ChunkedStaticCollisionWorld(
                bake.Manifest.WorldId,
                bake.Manifest.Revision,
                bake.Manifest.ChunkSize,
                bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)).ToArray());
        }

        // A paraboloid mound 3 m high around (18, -18) whose steepest slope stays below 45
        // degrees. Only casted float arithmetic, so both runtimes bake the same heights.
        private static CollisionHeightfieldAuthoringEntry Mound()
        {
            const int samples = 17;
            var heights = new float[samples * samples];
            for (var z = 0; z < samples; z++)
            {
                for (var x = 0; x < samples; x++)
                {
                    var offsetX = (float)(x - 8);
                    var offsetZ = (float)(z - 8);
                    var squared = (float)((float)(offsetX * offsetX) + (float)(offsetZ * offsetZ));
                    heights[(z * samples) + x] = Math.Max(0.01f, (float)(3f - (float)(0.06f * squared)));
                }
            }

            return new CollisionHeightfieldAuthoringEntry
            {
                SourcePath = "MovementDeterminism/Mound#TerrainCollider0",
                LayerMask = CollisionLayers.CharacterMovement,
                OriginX = 10f,
                OriginZ = -26f,
                SpacingX = 1f,
                SpacingZ = 1f,
                SamplesX = samples,
                SamplesZ = samples,
                Heights = CollisionAuthoringEncoding.EncodeFloats(heights),
                Holes = string.Empty
            };
        }

        // A bowl around the pond: a 2 m rim from a radius of 4.6 to 6.5 m, an outer slope of
        // about 37 degrees down to 0.05 m, and an inner slope of about 31 degrees down to
        // 0.05 m. Only casted float arithmetic, so both runtimes bake the same heights.
        private static CollisionHeightfieldAuthoringEntry Bowl()
        {
            const int samples = 21;
            var heights = new float[samples * samples];
            for (var z = 0; z < samples; z++)
            {
                for (var x = 0; x < samples; x++)
                {
                    var offsetX = (float)(x - 10);
                    var offsetZ = (float)(z - 10);
                    var radius = (float)Math.Sqrt((float)((float)(offsetX * offsetX) + (float)(offsetZ * offsetZ)));
                    float height;
                    if (radius >= 6.5f)
                    {
                        height = (float)(2f - (float)(0.745f * (float)(radius - 6.5f)));
                    }
                    else if (radius >= 4.6f)
                    {
                        height = 2f;
                    }
                    else
                    {
                        height = (float)(2f - (float)(0.6f * (float)(4.6f - radius)));
                    }

                    heights[(z * samples) + x] = Math.Max(0.05f, height);
                }
            }

            return new CollisionHeightfieldAuthoringEntry
            {
                SourcePath = "MovementDeterminism/Bowl#TerrainCollider0",
                LayerMask = CollisionLayers.CharacterMovement,
                OriginX = 9f,
                OriginZ = 9f,
                SpacingX = 1f,
                SpacingZ = 1f,
                SamplesX = samples,
                SamplesZ = samples,
                Heights = CollisionAuthoringEncoding.EncodeFloats(heights),
                Holes = string.Empty
            };
        }

        // A square surface 4.4 m from the center to each side, which reaches under the rim,
        // clockwise seen from above.
        private static CollisionWaterAuthoringEntry Pond()
        {
            return new CollisionWaterAuthoringEntry
            {
                SourcePath = "MovementDeterminism/Pond#WaterBody",
                BodyId = "movement-determinism.pond",
                Vertices = CollisionAuthoringEncoding.EncodeFloats(new[]
                {
                    14.6f, PondSurface, 14.6f,
                    14.6f, PondSurface, 23.4f,
                    23.4f, PondSurface, 23.4f,
                    23.4f, PondSurface, 14.6f
                }),
                Triangles = CollisionAuthoringEncoding.EncodeInts(new[] { 0, 1, 2, 0, 2, 3 })
            };
        }

        private static CollisionMeshAuthoringEntry Mesh(string name, float[] vertices, int[] triangles)
        {
            return new CollisionMeshAuthoringEntry
            {
                SourcePath = "MovementDeterminism/" + name + "#MeshCollider0",
                LayerMask = CollisionLayers.CharacterMovement,
                Vertices = CollisionAuthoringEncoding.EncodeFloats(vertices),
                Triangles = CollisionAuthoringEncoding.EncodeInts(triangles)
            };
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
            IReadOnlyList<SimulationVector3> starts,
            IReadOnlyList<MovementPathPlan> plans,
            ICollisionWorld collisionWorld,
            MovementSimulationSettings settings)
        {
            Fingerprint = fingerprint;
            Starts = starts;
            Plans = plans;
            CollisionWorld = collisionWorld;
            Settings = settings;
        }

        public string Fingerprint { get; }

        public IReadOnlyList<SimulationVector3> Starts { get; }

        public IReadOnlyList<MovementPathPlan> Plans { get; }

        public ICollisionWorld CollisionWorld { get; }

        public MovementSimulationSettings Settings { get; }
    }
}
