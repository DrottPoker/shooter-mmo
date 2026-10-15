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
        public const string ExpectedSimulationRevision = "movement-simulation-v4";
        public const string ExpectedFingerprint = "d8eddca55aba6de1";

        private const int TickCount = 900;

        // The route steers through every collision case: blocked by a wall, sliding
        // along it, climbing a rotated ramp and dropping off its high edge, stopping
        // at a rotated pillar, stepping up and jumping on a step, and walking off it.
        private static readonly Waypoint[] Route =
        {
            new Waypoint(10f, 0f, PlayerMovementButtons.None, 0, 60),
            new Waypoint(6f, 6f, PlayerMovementButtons.Sprint, 0, 50),
            new Waypoint(0f, 16f, PlayerMovementButtons.None, 0, 90),
            new Waypoint(0f, 3f, PlayerMovementButtons.Sprint, 0, 90),
            new Waypoint(2f, -8f, PlayerMovementButtons.None, 0, 90),
            new Waypoint(-6f, 0f, PlayerMovementButtons.None, 0, 90),
            new Waypoint(-6f, 0.5f, PlayerMovementButtons.None, 15, 45),
            new Waypoint(-12f, 0f, PlayerMovementButtons.Sprint, 0, 45)
        };

        public static string ComputeFingerprint()
        {
            return ComputeTrace().Fingerprint;
        }

        // The per-tick states let a failing runtime be compared tick by tick.
        public static MovementDeterminismTrace ComputeTrace()
        {
            var collisionWorld = CreateCollisionWorld();
            var settings = new MovementSimulationSettings(
                30,
                5f,
                8f,
                720f,
                -24f,
                7f,
                -2f,
                0f,
                -30f,
                30f,
                -30f,
                30f);
            var carryState = PlayerCarryState.Default;
            var queryBuffer = new CollisionQueryBuffer();
            var state = PlayerMovementSimulation.CreateInitialState(
                settings,
                collisionWorld,
                0f,
                0f,
                0f,
                0f);
            var states = new List<PlayerMovementState>(TickCount);
            var routeIndex = 0;
            var routeTick = 0;
            var hash = 14695981039346656037UL;
            for (uint tick = 1; tick <= TickCount; tick++)
            {
                var input = CreateInput(tick, state, ref routeIndex, ref routeTick);
                state = PlayerMovementSimulation.Step(
                    state,
                    input,
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

        private static PlayerMovementInput CreateInput(
            uint tick,
            PlayerMovementState state,
            ref int routeIndex,
            ref int routeTick)
        {
            while (routeIndex < Route.Length)
            {
                var waypoint = Route[routeIndex];
                var deltaX = (double)waypoint.X - state.PositionX;
                var deltaZ = (double)waypoint.Z - state.PositionZ;
                var reached = waypoint.JumpEveryTicks == 0
                    && ((deltaX * deltaX) + (deltaZ * deltaZ)) < 0.04d;
                if (reached || routeTick >= waypoint.TickBudget)
                {
                    routeIndex++;
                    routeTick = 0;
                    continue;
                }

                routeTick++;
                var yaw = (float)(Math.Atan2(deltaX, deltaZ) * (180d / Math.PI));
                var buttons = waypoint.Buttons;
                if (waypoint.JumpEveryTicks > 0 && routeTick % waypoint.JumpEveryTicks == 0)
                {
                    buttons |= PlayerMovementButtons.Jump;
                }

                return Input(tick, 0f, 1f, yaw, buttons);
            }

            // After the route: aim and strafe, then turn continuously with jumps.
            if (tick <= 760)
            {
                return Input(tick, 1f, 0.25f, 0f, PlayerMovementButtons.Aim);
            }

            var turningYaw = (tick * 4) % 360;
            var turningButtons = tick % 40 == 0
                ? PlayerMovementButtons.Jump
                : PlayerMovementButtons.Sprint;
            return Input(tick, 0.3f, 1f, turningYaw, turningButtons);
        }

        private static PlayerMovementInput Input(
            uint tick,
            float moveX,
            float moveY,
            float cameraYawDegrees,
            PlayerMovementButtons buttons)
        {
            return new PlayerMovementInput(tick, tick, moveX, moveY, cameraYawDegrees, buttons);
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
            hash = Mix(hash, (state.IsGrounded ? 1u : 0u) | (state.IsSprinting ? 2u : 0u));
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

        private readonly struct Waypoint
        {
            public Waypoint(
                float x,
                float z,
                PlayerMovementButtons buttons,
                int jumpEveryTicks,
                int tickBudget)
            {
                X = x;
                Z = z;
                Buttons = buttons;
                JumpEveryTicks = jumpEveryTicks;
                TickBudget = tickBudget;
            }

            public float X { get; }

            public float Z { get; }

            public PlayerMovementButtons Buttons { get; }

            public int JumpEveryTicks { get; }

            public int TickBudget { get; }
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
}
