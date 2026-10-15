#nullable disable
using System;
using System.Globalization;
using System.Linq;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.SharedTests
{
    // Compiled into both the Unity EditMode tests and the .NET backend tests, so the
    // client and server runtimes run the exact same movement scenario.
    public static class MovementDeterminismScenario
    {
        // Change both values together, and only for an intended change to shared movement
        // or collision behavior: bump GameSimulationCompatibility.Revision and pin the new
        // fingerprint. Clients and workers with different revisions must not interoperate.
        public const string ExpectedSimulationRevision = "movement-simulation-v4";
        public const string ExpectedFingerprint = "6ea4008adcad6388";

        private const int TickCount = 600;

        public static string ComputeFingerprint()
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
            var hash = 14695981039346656037UL;
            for (uint tick = 1; tick <= TickCount; tick++)
            {
                state = PlayerMovementSimulation.Step(
                    state,
                    CreateInput(tick),
                    settings,
                    carryState,
                    collisionWorld,
                    queryBuffer);
                hash = Mix(hash, state);
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // Walks, sprints diagonally into a wall, jumps, aims, climbs a ramp and a step,
        // and turns continuously, so every movement and collision branch contributes.
        private static PlayerMovementInput CreateInput(uint tick)
        {
            if (tick <= 60)
            {
                return Input(tick, 0f, 1f, 0f, PlayerMovementButtons.None);
            }

            if (tick <= 120)
            {
                return Input(tick, 0.7071068f, 0.7071068f, 30f, PlayerMovementButtons.Sprint);
            }

            if (tick <= 150)
            {
                return Input(
                    tick,
                    0f,
                    1f,
                    90f,
                    tick % 15 == 0 ? PlayerMovementButtons.Jump : PlayerMovementButtons.Sprint);
            }

            if (tick <= 240)
            {
                return Input(tick, -1f, 0f, 90f, PlayerMovementButtons.Aim);
            }

            if (tick <= 360)
            {
                return Input(tick, 0f, 1f, 180f, PlayerMovementButtons.Sprint);
            }

            var yaw = (tick * 3) % 360;
            var buttons = tick % 50 == 0
                ? PlayerMovementButtons.Jump
                : PlayerMovementButtons.None;
            return Input(tick, 0.25f, 1f, yaw, buttons);
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
                    Box("Wall", 6.5f, 1.5f, 3f, 0.5f, 1.5f, 5f, 0f, 0f, 0f, 1f),
                    // A ramp rotated by 20 degrees around X, written as exact literals.
                    Box("Ramp", -4f, 0.5f, -8f, 2f, 0.25f, 4f, 0.17364818f, 0f, 0f, 0.98480775f),
                    Box("Step", -4f, 0.15f, 4f, 2f, 0.15f, 2f, 0f, 0f, 0f, 1f),
                    Box("Pillar", 2f, 1f, 9f, 0.5f, 1f, 0.5f, 0f, 0.38268343f, 0f, 0.9238795f)
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
    }
}
