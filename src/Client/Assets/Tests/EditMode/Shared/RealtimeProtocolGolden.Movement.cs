#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Movement input, movement settings, player state, and the owner snapshot.
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
                () => RealtimeProtocol.EncodeMovementInputBatch(new[] { MovementInputModel(1) }));
            r.Vector(
                "Movement/MovementInputBatch/four",
                inputBatch,
                () => RealtimeProtocol.EncodeMovementInputBatch(new[]
                {
                    MovementInputModel(1),
                    new RealtimeMovementInput(
                        inputSequence: uint.MaxValue,
                        clientTick: 0,
                        moveX: -0.6f,
                        moveY: 0.8f,
                        cameraYawDegrees: -720.25f,
                        buttons: RealtimeMovementButtons.Sprint | RealtimeMovementButtons.Jump | RealtimeMovementButtons.Aim),
                    new RealtimeMovementInput(
                        inputSequence: 3,
                        clientTick: 0x01020304,
                        moveX: 0f,
                        moveY: -1f,
                        cameraYawDegrees: 1e30f,
                        buttons: RealtimeMovementButtons.Jump),
                    new RealtimeMovementInput(
                        inputSequence: 4,
                        clientTick: 77,
                        moveX: -0f,
                        moveY: 0f,
                        cameraYawDegrees: -0f,
                        buttons: RealtimeMovementButtons.None)
                }));
            r.Vector(
                "Movement/OwnerSimulationSnapshot/typical",
                ownerSnapshot,
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    snapshotSequence: 0x11223344,
                    serverTick: 0x55667788,
                    lastProcessedInputSequence: 0x99AABBCC,
                    state: PlayerStateModel())));
            r.Vector(
                "Movement/OwnerSimulationSnapshot/boundary",
                ownerSnapshot,
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    snapshotSequence: uint.MaxValue,
                    serverTick: 0,
                    lastProcessedInputSequence: 1,
                    state: PlayerStateBoundaryModel())));

            r.Accepts("Movement/MovementInputBatch/baseline", InputBatchWire(MovementInputWire()));
            r.Accepts(
                "Movement/MovementInputBatch/four inputs",
                InputBatchWire(Enumerable.Range(1, 4).Select(index => MovementInputWire(inputSequence: (uint)index)).ToArray()));
            r.Rejects("Movement/MovementInputBatch/zero inputs", InputBatchWire());
            r.Rejects(
                "Movement/MovementInputBatch/five inputs",
                InputBatchWire(Enumerable.Range(1, 5).Select(index => MovementInputWire(inputSequence: (uint)index)).ToArray()));
            r.Rejects(
                "Movement/MovementInputBatch/count 255 with one input",
                Wire.Message(inputBatch).U8(255).Append(MovementInputWire()));
            r.Accepts("Movement/MovementInputBatch/unit diagonal", InputBatchWire(MovementInputWire(moveX: 0.6f, moveY: -0.8f)));
            r.Accepts("Movement/MovementInputBatch/full axis", InputBatchWire(MovementInputWire(moveX: -1f, moveY: 0f)));
            r.Rejects("Movement/MovementInputBatch/axis above unit length", InputBatchWire(MovementInputWire(moveX: 1.01f, moveY: 0f)));
            r.Rejects("Movement/MovementInputBatch/diagonal above unit length", InputBatchWire(MovementInputWire(moveX: 0.75f, moveY: 0.75f)));
            r.Rejects("Movement/MovementInputBatch/NaN move", InputBatchWire(MovementInputWire(moveXBits: QuietNaNBits)));
            r.Rejects("Movement/MovementInputBatch/infinite move", InputBatchWire(MovementInputWire(moveYBits: NegativeInfinityBits)));
            r.Rejects("Movement/MovementInputBatch/NaN camera yaw", InputBatchWire(MovementInputWire(cameraYawBits: NegativeNaNBits)));
            r.Rejects("Movement/MovementInputBatch/infinite camera yaw", InputBatchWire(MovementInputWire(cameraYawBits: PositiveInfinityBits)));
            r.Accepts("Movement/MovementInputBatch/huge camera yaw", InputBatchWire(MovementInputWire(cameraYaw: float.MaxValue)));
            r.Accepts("Movement/MovementInputBatch/all buttons", InputBatchWire(MovementInputWire(buttons: 7)));
            r.Rejects("Movement/MovementInputBatch/unknown button bit", InputBatchWire(MovementInputWire(buttons: 8)));
            r.Rejects("Movement/MovementInputBatch/high button bit", InputBatchWire(MovementInputWire(buttons: 0x80)));

            r.Accepts("Movement/OwnerSimulationSnapshot/baseline", OwnerSnapshotWire(PlayerStateWire()));
            r.Accepts("Movement/OwnerSimulationSnapshot/grounded and sprinting", OwnerSnapshotWire(PlayerStateWire(flags: 3)));
            r.Accepts("Movement/OwnerSimulationSnapshot/no flags", OwnerSnapshotWire(PlayerStateWire(flags: 0)));
            r.Rejects("Movement/OwnerSimulationSnapshot/unknown flag bit", OwnerSnapshotWire(PlayerStateWire(flags: 4)));
            r.Rejects("Movement/OwnerSimulationSnapshot/high flag bit", OwnerSnapshotWire(PlayerStateWire(flags: 0x80)));
            r.Accepts(
                "Movement/OwnerSimulationSnapshot/negative zero and subnormal",
                OwnerSnapshotWire(PlayerStateWire(positionXBits: NegativeZeroBits, velocityZBits: SmallestSubnormalBits)));
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
                "Movement/MovementInputBatch/empty batch",
                () => RealtimeProtocol.EncodeMovementInputBatch(Array.Empty<RealtimeMovementInput>()));
            r.RejectsEncode(
                "Movement/MovementInputBatch/five inputs",
                () => RealtimeProtocol.EncodeMovementInputBatch(
                    Enumerable.Range(1, 5).Select(index => MovementInputModel(index)).ToArray()));
            r.RejectsEncode(
                "Movement/MovementInputBatch/null input",
                () => RealtimeProtocol.EncodeMovementInputBatch(new RealtimeMovementInput[] { null }));
            r.RejectsEncode(
                "Movement/MovementInputBatch/move above unit length",
                () => RealtimeProtocol.EncodeMovementInputBatch(new[] { MovementInputModel(1, moveX: 1f, moveY: 1f) }));
            r.RejectsEncode(
                "Movement/MovementInputBatch/NaN camera yaw",
                () => RealtimeProtocol.EncodeMovementInputBatch(new[] { MovementInputModel(1, cameraYaw: float.NaN) }));
            r.RejectsEncode(
                "Movement/MovementInputBatch/unknown button",
                () => RealtimeProtocol.EncodeMovementInputBatch(new[] { MovementInputModel(1, buttons: (RealtimeMovementButtons)8) }));
            r.RejectsEncode("Movement/OwnerSimulationSnapshot/null snapshot", () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(null));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/null state",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(1, 2, 3, null)));
            r.RejectsEncode(
                "Movement/OwnerSimulationSnapshot/infinite velocity",
                () => RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
                    1,
                    2,
                    3,
                    PlayerStateModel(velocityY: float.PositiveInfinity))));
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
            Accepts("sprint speed equal to walk speed", MovementSettingsWire(sprintSpeed: 5f));
            Rejects("sprint speed below walk speed", MovementSettingsWire(sprintSpeed: 4.5f));
            Rejects("rotation speed 0", MovementSettingsWire(rotationSpeedDegrees: 0f));
            Rejects("gravity 0", MovementSettingsWire(gravity: 0f));
            Rejects("positive gravity", MovementSettingsWire(gravity: 9.81f));
            Rejects("maximum fall speed 0", MovementSettingsWire(maximumFallSpeed: 0f));
            Rejects("jump velocity 0", MovementSettingsWire(jumpVelocity: 0f));
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

        private static RealtimeMovementInput MovementInputModel(
            int sequence,
            float moveX = 0.25f,
            float moveY = -0.5f,
            float cameraYaw = 135.75f,
            RealtimeMovementButtons buttons = RealtimeMovementButtons.Sprint)
        {
            return new RealtimeMovementInput(
                inputSequence: (uint)sequence,
                clientTick: (uint)(1000 + sequence),
                moveX: moveX,
                moveY: moveY,
                cameraYawDegrees: cameraYaw,
                buttons: buttons);
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
                isGrounded: true,
                isSprinting: false);
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
                isGrounded: false,
                isSprinting: true);
        }

        private static RealtimeMovementSettings MovementSettingsModel(ushort tickRateHz = 30)
        {
            return new RealtimeMovementSettings(
                tickRateHz: tickRateHz,
                snapshotRateHz: 15,
                walkSpeed: 5f,
                sprintSpeed: 8f,
                rotationSpeedDegrees: 720f,
                gravity: -24f,
                maximumFallSpeed: 55f,
                jumpVelocity: 7f,
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
                sprintSpeed: float.Epsilon,
                rotationSpeedDegrees: float.MaxValue,
                gravity: -float.Epsilon,
                maximumFallSpeed: 1e-30f,
                jumpVelocity: 1e30f,
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

        private static Wire MovementInputWire(
            uint inputSequence = 1,
            float moveX = 0.25f,
            float moveY = -0.5f,
            float cameraYaw = 135.75f,
            uint? moveXBits = null,
            uint? moveYBits = null,
            uint? cameraYawBits = null,
            byte buttons = 1)
        {
            return Wire.Fragment()
                .U32(inputSequence)
                .U32(1000 + inputSequence)
                .F32Or(moveX, moveXBits)
                .F32Or(moveY, moveYBits)
                .F32Or(cameraYaw, cameraYawBits)
                .U8(buttons);
        }

        private static Wire InputBatchWire(params Wire[] inputs)
        {
            var wire = Wire.Message(RealtimeMessageType.MovementInputBatch).U8((byte)inputs.Length);
            foreach (var input in inputs)
            {
                wire.Append(input);
            }

            return wire;
        }

        private static Wire OwnerSnapshotWire(Wire playerState)
        {
            return Wire.Message(RealtimeMessageType.OwnerSimulationSnapshot)
                .U32(0x11223344)
                .U32(0x55667788)
                .U32(0x99AABBCC)
                .Append(playerState);
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
            float sprintSpeed = 8f,
            float rotationSpeedDegrees = 720f,
            float gravity = -24f,
            float maximumFallSpeed = 55f,
            float jumpVelocity = 7f,
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
                .F32(sprintSpeed)
                .F32(rotationSpeedDegrees)
                .F32(gravity)
                .F32(maximumFallSpeed)
                .F32(jumpVelocity)
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
