#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Movement input and paths, movement settings, player state, and the owner snapshot.
    public static partial class RealtimeProtocolGolden
    {
        private const uint QuietNaNBits = 0x7FC00000;
        private const uint NegativeNaNBits = 0xFFC00001;
        private const uint PositiveInfinityBits = 0x7F800000;
        private const uint NegativeInfinityBits = 0xFF800000;
        private const uint NegativeZeroBits = 0x80000000;
        private const uint SmallestSubnormalBits = 0x00000001;

        private static void RegisterMovement(Registry r)
        {
            const RealtimeMessageType inputBatch = RealtimeMessageType.MovementInputBatch;
            const RealtimeMessageType ownerSnapshot = RealtimeMessageType.OwnerSimulationSnapshot;

            r.Vector(
                "Movement/MovementInputBatch/single",
                inputBatch,
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(null, MovementInputModel(1))));
            r.Vector(
                "Movement/MovementInputBatch/four",
                inputBatch,
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    null,
                    MovementInputModel(1),
                    new RealtimeMovementInput(inputSequence: uint.MaxValue, clientTick: 0, pathId: 0),
                    new RealtimeMovementInput(inputSequence: 3, clientTick: 0x01020304, pathId: uint.MaxValue),
                    new RealtimeMovementInput(inputSequence: 4, clientTick: 77, pathId: 0x0A0B0C0D))));
            r.Vector(
                "Movement/MovementInputBatch/with path",
                inputBatch,
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(
                        0x0A0B0C0D,
                        new RealtimeMovementPathWaypoint(12.5f, -0f, -3.25f),
                        new RealtimeMovementPathWaypoint(float.MaxValue, float.Epsilon, float.MinValue)),
                    MovementInputModel(1),
                    new RealtimeMovementInput(inputSequence: 2, clientTick: 1002, pathId: 0x0A0B0C0D))));
            r.Vector(
                "Movement/MovementInputBatch/full path",
                inputBatch,
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(7, Enumerable.Range(0, 32)
                        .Select(index => new RealtimeMovementPathWaypoint(index * 0.5f, index * -0.25f, 100f - index))
                        .ToArray()),
                    new RealtimeMovementInput(inputSequence: 9, clientTick: 9, pathId: 7))));
            r.Vector(
                "Movement/OwnerSimulationSnapshot/typical",
                ownerSnapshot,
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    snapshotSequence: 0x11223344,
                    serverTick: 0x55667788,
                    lastProcessedInputSequence: 0x99AABBCC,
                    state: PlayerStateModel(),
                    pathProgress: new RealtimeMovementPathProgress(0x0A0B0C0D, 3, 2))));
            r.Vector(
                "Movement/OwnerSimulationSnapshot/boundary",
                ownerSnapshot,
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    snapshotSequence: uint.MaxValue,
                    serverTick: 0,
                    lastProcessedInputSequence: 1,
                    state: PlayerStateBoundaryModel(),
                    pathProgress: new RealtimeMovementPathProgress(uint.MaxValue, 32, 255))));

            r.Accepts("Movement/MovementInputBatch/baseline", InputBatchWire(null, MovementInputWire()));
            r.Accepts(
                "Movement/MovementInputBatch/four inputs",
                InputBatchWire(null, Enumerable.Range(1, 4).Select(index => MovementInputWire(inputSequence: (uint)index)).ToArray()));
            r.Rejects("Movement/MovementInputBatch/zero inputs", InputBatchWire(null));
            r.Rejects(
                "Movement/MovementInputBatch/five inputs",
                InputBatchWire(null, Enumerable.Range(1, 5).Select(index => MovementInputWire(inputSequence: (uint)index)).ToArray()));
            r.Rejects(
                "Movement/MovementInputBatch/count 255 with one input",
                Wire.Message(inputBatch).U8(255).Append(MovementInputWire()).Bool(false));
            r.Rejects(
                "Movement/MovementInputBatch/missing path presence",
                Wire.Message(inputBatch).U8(1).Append(MovementInputWire()));
            r.Accepts("Movement/MovementInputBatch/path id 0 stands", InputBatchWire(null, MovementInputWire(pathId: 0)));
            r.Accepts(
                "Movement/MovementInputBatch/one waypoint",
                InputBatchWire(PathWire(5, WaypointWire()), MovementInputWire(pathId: 5)));
            r.Accepts(
                "Movement/MovementInputBatch/32 waypoints",
                InputBatchWire(PathWire(5, Enumerable.Repeat(0, 32).Select(_ => WaypointWire()).ToArray()), MovementInputWire(pathId: 5)));
            r.Rejects(
                "Movement/MovementInputBatch/33 waypoints",
                InputBatchWire(PathWire(5, Enumerable.Repeat(0, 33).Select(_ => WaypointWire()).ToArray()), MovementInputWire(pathId: 5)));
            r.Rejects("Movement/MovementInputBatch/path without waypoints", InputBatchWire(PathWire(5), MovementInputWire(pathId: 5)));
            r.Rejects("Movement/MovementInputBatch/path id 0", InputBatchWire(PathWire(0, WaypointWire()), MovementInputWire(pathId: 0)));
            r.Accepts(
                "Movement/MovementInputBatch/path of the newest input",
                InputBatchWire(
                    PathWire(6, WaypointWire()),
                    MovementInputWire(inputSequence: 1, pathId: 5),
                    MovementInputWire(inputSequence: 2, pathId: 6)));
            r.Rejects(
                "Movement/MovementInputBatch/path of an older input",
                InputBatchWire(
                    PathWire(5, WaypointWire()),
                    MovementInputWire(inputSequence: 1, pathId: 5),
                    MovementInputWire(inputSequence: 2, pathId: 6)));
            r.Rejects(
                "Movement/MovementInputBatch/path while the newest input stands",
                InputBatchWire(PathWire(5, WaypointWire()), MovementInputWire(pathId: 0)));
            r.AcceptsAs(
                "Movement/MovementInputBatch/non-zero path presence",
                Wire.Message(inputBatch).U8(1).Append(MovementInputWire(pathId: 5)).U8(2).Append(PathWire(5, WaypointWire())),
                InputBatchWire(PathWire(5, WaypointWire()), MovementInputWire(pathId: 5)));
            r.Accepts(
                "Movement/MovementInputBatch/negative zero and subnormal waypoint",
                InputBatchWire(
                    PathWire(5, WaypointWire(xBits: NegativeZeroBits, zBits: SmallestSubnormalBits)),
                    MovementInputWire(pathId: 5)));
            var axisNames = new[] { "x", "y", "z" };
            for (var axis = 0; axis < axisNames.Length; axis++)
            {
                r.Rejects(
                    "Movement/MovementInputBatch/NaN waypoint " + axisNames[axis],
                    InputBatchWire(PathWire(5, WaypointWireWithBits(axis, QuietNaNBits)), MovementInputWire(pathId: 5)));
                r.Rejects(
                    "Movement/MovementInputBatch/infinite waypoint " + axisNames[axis],
                    InputBatchWire(PathWire(5, WaypointWireWithBits(axis, PositiveInfinityBits)), MovementInputWire(pathId: 5)));
            }

            r.Accepts("Movement/OwnerSimulationSnapshot/baseline", OwnerSnapshotWire(PlayerStateWire()));
            r.Accepts("Movement/OwnerSimulationSnapshot/no flags", OwnerSnapshotWire(PlayerStateWire(flags: 0)));
            r.Rejects("Movement/OwnerSimulationSnapshot/removed sprint flag bit", OwnerSnapshotWire(PlayerStateWire(flags: 2)));
            r.Rejects("Movement/OwnerSimulationSnapshot/high flag bit", OwnerSnapshotWire(PlayerStateWire(flags: 0x80)));
            r.Accepts(
                "Movement/OwnerSimulationSnapshot/negative zero and subnormal",
                OwnerSnapshotWire(PlayerStateWire(positionXBits: NegativeZeroBits, velocityZBits: SmallestSubnormalBits)));
            r.Accepts(
                "Movement/OwnerSimulationSnapshot/path ended at 32 waypoints",
                OwnerSnapshotWire(PlayerStateWire(), PathProgressWire(nextWaypointIndex: 32, blockedTicks: 255)));
            r.Rejects(
                "Movement/OwnerSimulationSnapshot/waypoint index above 32",
                OwnerSnapshotWire(PlayerStateWire(), PathProgressWire(nextWaypointIndex: 33)));
            r.Accepts(
                "Movement/OwnerSimulationSnapshot/no path",
                OwnerSnapshotWire(PlayerStateWire(), PathProgressWire(pathId: 0, nextWaypointIndex: 0)));
            r.Rejects(
                "Movement/OwnerSimulationSnapshot/waypoint index without a path",
                OwnerSnapshotWire(PlayerStateWire(), PathProgressWire(pathId: 0, nextWaypointIndex: 1)));
            r.Rejects(
                "Movement/OwnerSimulationSnapshot/blocked ticks without a path",
                OwnerSnapshotWire(PlayerStateWire(), PathProgressWire(pathId: 0, nextWaypointIndex: 0, blockedTicks: 1)));
            r.Rejects(
                "Movement/OwnerSimulationSnapshot/missing path progress",
                Wire.Message(ownerSnapshot).U32(0x11223344).U32(0x55667788).U32(0x99AABBCC).Append(PlayerStateWire()));
            var floatNames = new[] { "position x", "position y", "position z", "velocity x", "velocity y", "velocity z", "yaw" };
            for (var index = 0; index < floatNames.Length; index++)
            {
                r.Rejects(
                    "Movement/OwnerSimulationSnapshot/NaN " + floatNames[index],
                    OwnerSnapshotWire(PlayerStateWireWithBits(index, QuietNaNBits)));
                r.Rejects(
                    "Movement/OwnerSimulationSnapshot/infinite " + floatNames[index],
                    OwnerSnapshotWire(PlayerStateWireWithBits(index, NegativeInfinityBits)));
            }

            r.RejectsEncode("Movement/MovementInputBatch/null batch", () => RealtimeProtocol.EncodeMovementInputBatch(null));
            r.RejectsEncode(
                "Movement/MovementInputBatch/null inputs",
                () => RealtimeProtocol.EncodeMovementInputBatch(new RealtimeMovementInputBatch(null, null)));
            r.RejectsEncode(
                "Movement/MovementInputBatch/empty batch",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(null)));
            r.RejectsEncode(
                "Movement/MovementInputBatch/five inputs",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    null,
                    Enumerable.Range(1, 5).Select(index => MovementInputModel(index)).ToArray())));
            r.RejectsEncode(
                "Movement/MovementInputBatch/null input",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(null, new RealtimeMovementInput[] { null })));
            r.RejectsEncode(
                "Movement/MovementInputBatch/path of another input",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(4, new RealtimeMovementPathWaypoint(1f, 0f, 1f)),
                    MovementInputModel(1))));
            r.RejectsEncode(
                "Movement/MovementInputBatch/path id 0",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(0, new RealtimeMovementPathWaypoint(1f, 0f, 1f)),
                    new RealtimeMovementInput(1, 1, 0))));
            r.RejectsEncode(
                "Movement/MovementInputBatch/no waypoints",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(5),
                    new RealtimeMovementInput(1, 1, 5))));
            r.RejectsEncode(
                "Movement/MovementInputBatch/33 waypoints",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(5, Enumerable.Repeat(new RealtimeMovementPathWaypoint(1f, 0f, 1f), 33).ToArray()),
                    new RealtimeMovementInput(1, 1, 5))));
            r.RejectsEncode(
                "Movement/MovementInputBatch/NaN waypoint",
                () => RealtimeProtocol.EncodeMovementInputBatch(InputBatchModel(
                    PathModel(5, new RealtimeMovementPathWaypoint(1f, float.NaN, 1f)),
                    new RealtimeMovementInput(1, 1, 5))));
            r.RejectsEncode("Movement/OwnerSimulationSnapshot/null snapshot", () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(null));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/null state",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    1,
                    2,
                    3,
                    null,
                    new RealtimeMovementPathProgress(0, 0, 0))));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/null path progress",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    1,
                    2,
                    3,
                    PlayerStateModel(),
                    null)));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/waypoint index above 32",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    1,
                    2,
                    3,
                    PlayerStateModel(),
                    new RealtimeMovementPathProgress(1, 33, 0))));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/progress without a path",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    1,
                    2,
                    3,
                    PlayerStateModel(),
                    new RealtimeMovementPathProgress(0, 1, 0))));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/infinite velocity",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    1,
                    2,
                    3,
                    PlayerStateModel(velocityY: float.PositiveInfinity),
                    new RealtimeMovementPathProgress(0, 0, 0))));
        }

        // Movement settings reach the wire only inside JoinAccepted.
        private static void RegisterMovementSettingsCases(Registry r)
        {
            void Accepts(string name, Wire settings)
            {
                r.Accepts("Session/JoinAccepted/settings " + name, JoinAcceptedWire(movementSettings: settings));
            }

            void Rejects(string name, Wire settings)
            {
                r.Rejects("Session/JoinAccepted/settings " + name, JoinAcceptedWire(movementSettings: settings));
            }

            Accepts("tick rate 120", MovementSettingsWire(tickRateHz: 120));
            Rejects("tick rate 0", MovementSettingsWire(tickRateHz: 0));
            Rejects("tick rate 121", MovementSettingsWire(tickRateHz: 121));
            Accepts("snapshot rate equal to tick rate", MovementSettingsWire(snapshotRateHz: 30));
            Rejects("snapshot rate 0", MovementSettingsWire(snapshotRateHz: 0));
            Rejects("snapshot rate above tick rate", MovementSettingsWire(snapshotRateHz: 60));
            Rejects("snapshot rate not dividing tick rate", MovementSettingsWire(snapshotRateHz: 20));
            Rejects("walk speed 0", MovementSettingsWire(walkSpeed: 0f));
            Rejects("NaN walk speed", MovementSettingsWire(walkSpeedBits: QuietNaNBits));
            Accepts("tiny walk speed", MovementSettingsWire(walkSpeed: float.Epsilon));
            Rejects("rotation speed 0", MovementSettingsWire(rotationSpeedDegrees: 0f));
            Rejects("gravity 0", MovementSettingsWire(gravity: 0f));
            Rejects("positive gravity", MovementSettingsWire(gravity: 9.81f));
            Rejects("maximum fall speed 0", MovementSettingsWire(maximumFallSpeed: 0f));
            Accepts("grounded vertical velocity 0", MovementSettingsWire(groundedVerticalVelocity: 0f));
            Rejects("positive grounded vertical velocity", MovementSettingsWire(groundedVerticalVelocity: 0.5f));
            Accepts("negative ground height", MovementSettingsWire(groundHeight: -1000f));
            Rejects("infinite ground height", MovementSettingsWire(groundHeightBits: PositiveInfinityBits));
            Rejects("minimum x equal to maximum x", MovementSettingsWire(minimumX: 14.5f));
            Rejects("minimum z above maximum z", MovementSettingsWire(minimumZ: 20f));
            Rejects("character radius 0", MovementSettingsWire(characterRadius: 0f));
            Accepts("character height twice the radius", MovementSettingsWire(characterHeight: 0.7f));
            Rejects("character height below twice the radius", MovementSettingsWire(characterHeight: 0.6f));
            Accepts("step height 0", MovementSettingsWire(stepHeight: 0f));
            Rejects("negative step height", MovementSettingsWire(stepHeight: -0.1f));
            Rejects("step height equal to character height", MovementSettingsWire(stepHeight: 2f));
            Accepts("slope 88.5 degrees", MovementSettingsWire(maximumSlopeDegrees: 88.5f));
            Rejects("slope 0 degrees", MovementSettingsWire(maximumSlopeDegrees: 0f));
            Rejects("slope 89 degrees", MovementSettingsWire(maximumSlopeDegrees: 89f));
            Accepts("ground snap 0", MovementSettingsWire(groundSnapDistance: 0f));
            Rejects("negative ground snap", MovementSettingsWire(groundSnapDistance: -0.1f));
            Accepts("substep equal to radius", MovementSettingsWire(maximumSubstepDistance: 0.35f));
            Rejects("substep 0", MovementSettingsWire(maximumSubstepDistance: 0f));
            Rejects("substep above radius", MovementSettingsWire(maximumSubstepDistance: 0.5f));
            Accepts("16 penetration iterations", MovementSettingsWire(maximumPenetrationIterations: 16));
            Rejects("0 penetration iterations", MovementSettingsWire(maximumPenetrationIterations: 0));
            Rejects("17 penetration iterations", MovementSettingsWire(maximumPenetrationIterations: 17));
        }

        private static RealtimeMovementInput MovementInputModel(int sequence, uint pathId = 0)
        {
            return new RealtimeMovementInput(
                inputSequence: (uint)sequence,
                clientTick: (uint)(1000 + sequence),
                pathId: pathId);
        }

        private static RealtimeMovementInputBatch InputBatchModel(
            RealtimeMovementPath path,
            params RealtimeMovementInput[] inputs)
        {
            return new RealtimeMovementInputBatch(inputs, path);
        }

        private static RealtimeMovementPath PathModel(uint pathId, params RealtimeMovementPathWaypoint[] waypoints)
        {
            return new RealtimeMovementPath(pathId, waypoints);
        }

        private static RealtimePlayerState PlayerStateModel(float velocityY = -9.75f)
        {
            return new RealtimePlayerState(
                positionX: 12.5f,
                positionY: -3.25f,
                positionZ: 1024.75f,
                velocityX: 0.5f,
                velocityY: velocityY,
                velocityZ: 2.125f,
                yawDegrees: 270.5f,
                isGrounded: true);
        }

        private static RealtimePlayerState PlayerStateBoundaryModel()
        {
            return new RealtimePlayerState(
                positionX: -0f,
                positionY: float.Epsilon,
                positionZ: float.MaxValue,
                velocityX: float.MinValue,
                velocityY: -float.Epsilon,
                velocityZ: 1e-38f,
                yawDegrees: -359.999f,
                isGrounded: false);
        }

        private static RealtimeMovementSettings MovementSettingsModel(ushort tickRateHz = 30)
        {
            return new RealtimeMovementSettings(
                tickRateHz: tickRateHz,
                snapshotRateHz: 15,
                walkSpeed: 5f,
                rotationSpeedDegrees: 720f,
                gravity: -24f,
                maximumFallSpeed: 55f,
                groundedVerticalVelocity: -2f,
                groundHeight: 0.25f,
                minimumX: -14f,
                maximumX: 14.5f,
                minimumZ: -13f,
                maximumZ: 13.5f,
                characterRadius: 0.35f,
                characterHeight: 2f,
                stepHeight: 0.3f,
                maximumSlopeDegrees: 45f,
                groundSnapDistance: 0.4f,
                maximumSubstepDistance: 0.1f,
                maximumPenetrationIterations: 6);
        }

        private static RealtimeMovementSettings MovementSettingsBoundaryModel()
        {
            return new RealtimeMovementSettings(
                tickRateHz: 120,
                snapshotRateHz: 120,
                walkSpeed: float.Epsilon,
                rotationSpeedDegrees: float.MaxValue,
                gravity: -float.Epsilon,
                maximumFallSpeed: 1e-30f,
                groundedVerticalVelocity: -0f,
                groundHeight: float.MinValue,
                minimumX: -1e9f,
                maximumX: -1e8f,
                minimumZ: 1e8f,
                maximumZ: 1e9f,
                characterRadius: 0.5f,
                characterHeight: 1f,
                stepHeight: 0f,
                maximumSlopeDegrees: 88.99f,
                groundSnapDistance: 0f,
                maximumSubstepDistance: 0.5f,
                maximumPenetrationIterations: 16);
        }

        private static Wire MovementInputWire(uint inputSequence = 1, uint pathId = 0)
        {
            return Wire.Fragment()
                .U32(inputSequence)
                .U32(1000 + inputSequence)
                .U32(pathId);
        }

        private static Wire InputBatchWire(Wire path, params Wire[] inputs)
        {
            var wire = Wire.Message(RealtimeMessageType.MovementInputBatch).U8((byte)inputs.Length);
            foreach (var input in inputs)
            {
                wire.Append(input);
            }

            wire.Bool(path != null);
            return path == null ? wire : wire.Append(path);
        }

        private static Wire PathWire(uint pathId, params Wire[] waypoints)
        {
            var wire = Wire.Fragment().U32(pathId).U8((byte)waypoints.Length);
            foreach (var waypoint in waypoints)
            {
                wire.Append(waypoint);
            }

            return wire;
        }

        private static Wire WaypointWire(uint? xBits = null, uint? zBits = null)
        {
            return Wire.Fragment().F32Or(4.5f, xBits).F32(0.25f).F32Or(-7.75f, zBits);
        }

        // The waypoint with the coordinate at the given axis replaced by raw bits.
        private static Wire WaypointWireWithBits(int axis, uint bits)
        {
            var values = new[] { 4.5f, 0.25f, -7.75f };
            var wire = Wire.Fragment();
            for (var index = 0; index < values.Length; index++)
            {
                wire.F32Or(values[index], index == axis ? bits : (uint?)null);
            }

            return wire;
        }

        private static Wire PathProgressWire(uint pathId = 3, byte nextWaypointIndex = 1, byte blockedTicks = 0)
        {
            return Wire.Fragment().U32(pathId).U8(nextWaypointIndex).U8(blockedTicks);
        }

        private static Wire OwnerSnapshotWire(Wire playerState, Wire pathProgress = null)
        {
            return Wire.Message(RealtimeMessageType.OwnerSimulationSnapshot)
                .U32(0x11223344)
                .U32(0x55667788)
                .U32(0x99AABBCC)
                .Append(playerState)
                .Append(pathProgress ?? PathProgressWire());
        }

        private static Wire PlayerStateWire(
            uint? positionXBits = null,
            float positionY = -3.25f,
            uint? velocityZBits = null,
            byte flags = 1)
        {
            return Wire.Fragment()
                .F32Or(12.5f, positionXBits)
                .F32(positionY)
                .F32(1024.75f)
                .F32(0.5f)
                .F32(-9.75f)
                .F32Or(2.125f, velocityZBits)
                .F32(270.5f)
                .U8(flags);
        }

        // The player state with the float at the given index replaced by raw bits.
        private static Wire PlayerStateWireWithBits(int floatIndex, uint bits)
        {
            var values = new[] { 12.5f, -3.25f, 1024.75f, 0.5f, -9.75f, 2.125f, 270.5f };
            var wire = Wire.Fragment();
            for (var index = 0; index < values.Length; index++)
            {
                wire.F32Or(values[index], index == floatIndex ? bits : (uint?)null);
            }

            return wire.U8(1);
        }

        private static Wire MovementSettingsWire(
            ushort tickRateHz = 30,
            ushort snapshotRateHz = 15,
            float walkSpeed = 5f,
            uint? walkSpeedBits = null,
            float rotationSpeedDegrees = 720f,
            float gravity = -24f,
            float maximumFallSpeed = 55f,
            float groundedVerticalVelocity = -2f,
            float groundHeight = 0.25f,
            uint? groundHeightBits = null,
            float minimumX = -14f,
            float maximumX = 14.5f,
            float minimumZ = -13f,
            float maximumZ = 13.5f,
            float characterRadius = 0.35f,
            float characterHeight = 2f,
            float stepHeight = 0.3f,
            float maximumSlopeDegrees = 45f,
            float groundSnapDistance = 0.4f,
            float maximumSubstepDistance = 0.1f,
            byte maximumPenetrationIterations = 6)
        {
            return Wire.Fragment()
                .U16(tickRateHz)
                .U16(snapshotRateHz)
                .F32Or(walkSpeed, walkSpeedBits)
                .F32(rotationSpeedDegrees)
                .F32(gravity)
                .F32(maximumFallSpeed)
                .F32(groundedVerticalVelocity)
                .F32Or(groundHeight, groundHeightBits)
                .F32(minimumX)
                .F32(maximumX)
                .F32(minimumZ)
                .F32(maximumZ)
                .F32(characterRadius)
                .F32(characterHeight)
                .F32(stepHeight)
                .F32(maximumSlopeDegrees)
                .F32(groundSnapDistance)
                .F32(maximumSubstepDistance)
                .U8(maximumPenetrationIterations);
        }
    }
}
