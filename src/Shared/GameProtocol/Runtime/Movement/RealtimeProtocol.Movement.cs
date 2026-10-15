using System;
using System.IO;

namespace Mmorpg.GameProtocol
{
    // Movement input, movement settings, and the owner's authoritative snapshot.
    public static partial class RealtimeProtocol
    {
        public const int OwnerSimulationSnapshotPacketSize = 48;

        public static byte[] EncodeMovementInputBatch(RealtimeMovementInput[] inputs)
        {
            if (inputs == null || inputs.Length == 0 || inputs.Length > MaximumInputBatchSize)
            {
                throw new ArgumentException(
                    $"Movement input batches require between 1 and {MaximumInputBatchSize} commands.",
                    nameof(inputs));
            }

            return Encode(RealtimeMessageType.MovementInputBatch, writer =>
            {
                writer.Write((byte)inputs.Length);
                for (var index = 0; index < inputs.Length; index++)
                {
                    WriteMovementInput(writer, inputs[index]);
                }
            });
        }

        public static bool TryDecodeMovementInputBatch(
            byte[] data,
            out RealtimeMovementInput[] inputs,
            out string error)
        {
            inputs = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.MovementInputBatch,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadByte(reader, out var count, out error)
                    || count == 0
                    || count > MaximumInputBatchSize)
                {
                    error = "Movement input batch size is invalid.";
                    return false;
                }

                var decoded = new RealtimeMovementInput[count];
                for (var index = 0; index < decoded.Length; index++)
                {
                    if (!TryReadMovementInput(reader, out decoded[index], out error))
                    {
                        return false;
                    }
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                inputs = decoded;
                return true;
            }
        }

        public static byte[] EncodeOwnerSimulationSnapshot(
            RealtimeOwnerSimulationSnapshot snapshot)
        {
            if (snapshot == null || !IsValidPlayerState(snapshot.State))
            {
                throw new ArgumentException(
                    "Owner simulation snapshot metadata is invalid.",
                    nameof(snapshot));
            }

            return Encode(RealtimeMessageType.OwnerSimulationSnapshot, writer =>
            {
                writer.Write(snapshot.SnapshotSequence);
                writer.Write(snapshot.ServerTick);
                writer.Write(snapshot.LastProcessedInputSequence);
                WritePlayerState(writer, snapshot.State);
            });
        }

        public static bool TryDecodeOwnerSimulationSnapshot(
            byte[] data,
            out RealtimeOwnerSimulationSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.OwnerSimulationSnapshot,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt32(reader, out var snapshotSequence, out error)
                    || !TryReadUInt32(reader, out var serverTick, out error)
                    || !TryReadUInt32(
                        reader,
                        out var lastProcessedInputSequence,
                        out error)
                    || !TryReadPlayerState(reader, out var state, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                snapshot = new RealtimeOwnerSimulationSnapshot(
                    snapshotSequence,
                    serverTick,
                    lastProcessedInputSequence,
                    state);
                return true;
            }
        }

        private static void WriteMovementInput(BinaryWriter writer, RealtimeMovementInput input)
        {
            if (input == null
                || !IsUnitInput(input.MoveX, input.MoveY)
                || !IsFinite(input.CameraYawDegrees)
                || !AreValidMovementButtons(input.Buttons))
            {
                throw new ArgumentException("Movement input is invalid.", nameof(input));
            }

            writer.Write(input.InputSequence);
            writer.Write(input.ClientTick);
            writer.Write(input.MoveX);
            writer.Write(input.MoveY);
            writer.Write(input.CameraYawDegrees);
            writer.Write((byte)input.Buttons);
        }

        private static bool TryReadMovementInput(
            BinaryReader reader,
            out RealtimeMovementInput input,
            out string error)
        {
            input = null;
            if (!TryReadUInt32(reader, out var inputSequence, out error)
                || !TryReadUInt32(reader, out var clientTick, out error)
                || !TryReadSingle(reader, out var moveX, out error)
                || !TryReadSingle(reader, out var moveY, out error)
                || !TryReadSingle(reader, out var cameraYaw, out error)
                || !TryReadByte(reader, out var rawButtons, out error))
            {
                return false;
            }

            var buttons = (RealtimeMovementButtons)rawButtons;
            if (!IsUnitInput(moveX, moveY) || !AreValidMovementButtons(buttons))
            {
                error = "Movement input values are invalid.";
                return false;
            }

            input = new RealtimeMovementInput(
                inputSequence,
                clientTick,
                moveX,
                moveY,
                cameraYaw,
                buttons);
            return true;
        }

        private static void WriteMovementSettings(BinaryWriter writer, RealtimeMovementSettings settings)
        {
            if (!AreValidMovementSettings(settings))
            {
                throw new ArgumentException("Movement settings are invalid.", nameof(settings));
            }

            writer.Write(settings.TickRateHz);
            writer.Write(settings.SnapshotRateHz);
            writer.Write(settings.WalkSpeed);
            writer.Write(settings.SprintSpeed);
            writer.Write(settings.RotationSpeedDegrees);
            writer.Write(settings.Gravity);
            writer.Write(settings.MaximumFallSpeed);
            writer.Write(settings.JumpVelocity);
            writer.Write(settings.GroundedVerticalVelocity);
            writer.Write(settings.GroundHeight);
            writer.Write(settings.MinimumX);
            writer.Write(settings.MaximumX);
            writer.Write(settings.MinimumZ);
            writer.Write(settings.MaximumZ);
            writer.Write(settings.CharacterRadius);
            writer.Write(settings.CharacterHeight);
            writer.Write(settings.StepHeight);
            writer.Write(settings.MaximumSlopeDegrees);
            writer.Write(settings.GroundSnapDistance);
            writer.Write(settings.MaximumSubstepDistance);
            writer.Write(settings.MaximumPenetrationIterations);
        }

        private static bool TryReadMovementSettings(
            BinaryReader reader,
            out RealtimeMovementSettings settings,
            out string error)
        {
            settings = null;
            if (!TryReadUInt16(reader, out var tickRateHz, out error)
                || !TryReadUInt16(reader, out var snapshotRateHz, out error)
                || !TryReadSingle(reader, out var walkSpeed, out error)
                || !TryReadSingle(reader, out var sprintSpeed, out error)
                || !TryReadSingle(reader, out var rotationSpeed, out error)
                || !TryReadSingle(reader, out var gravity, out error)
                || !TryReadSingle(reader, out var maximumFallSpeed, out error)
                || !TryReadSingle(reader, out var jumpVelocity, out error)
                || !TryReadSingle(reader, out var groundedVerticalVelocity, out error)
                || !TryReadSingle(reader, out var groundHeight, out error)
                || !TryReadSingle(reader, out var minimumX, out error)
                || !TryReadSingle(reader, out var maximumX, out error)
                || !TryReadSingle(reader, out var minimumZ, out error)
                || !TryReadSingle(reader, out var maximumZ, out error)
                || !TryReadSingle(reader, out var characterRadius, out error)
                || !TryReadSingle(reader, out var characterHeight, out error)
                || !TryReadSingle(reader, out var stepHeight, out error)
                || !TryReadSingle(reader, out var maximumSlopeDegrees, out error)
                || !TryReadSingle(reader, out var groundSnapDistance, out error)
                || !TryReadSingle(reader, out var maximumSubstepDistance, out error)
                || !TryReadByte(reader, out var maximumPenetrationIterations, out error))
            {
                return false;
            }

            var decoded = new RealtimeMovementSettings(
                tickRateHz,
                snapshotRateHz,
                walkSpeed,
                sprintSpeed,
                rotationSpeed,
                gravity,
                maximumFallSpeed,
                jumpVelocity,
                groundedVerticalVelocity,
                groundHeight,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ,
                characterRadius,
                characterHeight,
                stepHeight,
                maximumSlopeDegrees,
                groundSnapDistance,
                maximumSubstepDistance,
                maximumPenetrationIterations);
            if (!AreValidMovementSettings(decoded))
            {
                error = "Movement settings are invalid.";
                return false;
            }

            settings = decoded;
            return true;
        }

        private static void WritePlayerState(BinaryWriter writer, RealtimePlayerState state)
        {
            if (!IsValidPlayerState(state))
            {
                throw new ArgumentException("Player movement state is invalid.", nameof(state));
            }

            writer.Write(state.PositionX);
            writer.Write(state.PositionY);
            writer.Write(state.PositionZ);
            writer.Write(state.VelocityX);
            writer.Write(state.VelocityY);
            writer.Write(state.VelocityZ);
            writer.Write(state.YawDegrees);
            byte flags = 0;
            if (state.IsGrounded)
            {
                flags |= 1;
            }

            if (state.IsSprinting)
            {
                flags |= 2;
            }

            writer.Write(flags);
        }

        private static bool TryReadPlayerState(
            BinaryReader reader,
            out RealtimePlayerState state,
            out string error)
        {
            state = null;
            if (!TryReadSingle(reader, out var positionX, out error)
                || !TryReadSingle(reader, out var positionY, out error)
                || !TryReadSingle(reader, out var positionZ, out error)
                || !TryReadSingle(reader, out var velocityX, out error)
                || !TryReadSingle(reader, out var velocityY, out error)
                || !TryReadSingle(reader, out var velocityZ, out error)
                || !TryReadSingle(reader, out var yawDegrees, out error)
                || !TryReadByte(reader, out var flags, out error))
            {
                return false;
            }

            if ((flags & ~3) != 0)
            {
                error = "Player movement state flags are invalid.";
                return false;
            }

            var decoded = new RealtimePlayerState(
                positionX,
                positionY,
                positionZ,
                velocityX,
                velocityY,
                velocityZ,
                yawDegrees,
                (flags & 1) != 0,
                (flags & 2) != 0);
            if (!IsValidPlayerState(decoded))
            {
                error = "Player movement state is invalid.";
                return false;
            }

            state = decoded;
            return true;
        }

        private static bool AreValidMovementSettings(RealtimeMovementSettings settings)
        {
            return settings != null
                && settings.TickRateHz > 0
                && settings.TickRateHz <= 120
                && settings.SnapshotRateHz > 0
                && settings.SnapshotRateHz <= settings.TickRateHz
                && settings.TickRateHz % settings.SnapshotRateHz == 0
                && IsFinite(settings.WalkSpeed)
                && settings.WalkSpeed > 0f
                && IsFinite(settings.SprintSpeed)
                && settings.SprintSpeed >= settings.WalkSpeed
                && IsFinite(settings.RotationSpeedDegrees)
                && settings.RotationSpeedDegrees > 0f
                && IsFinite(settings.Gravity)
                && settings.Gravity < 0f
                && IsFinite(settings.MaximumFallSpeed)
                && settings.MaximumFallSpeed > 0f
                && IsFinite(settings.JumpVelocity)
                && settings.JumpVelocity > 0f
                && IsFinite(settings.GroundedVerticalVelocity)
                && settings.GroundedVerticalVelocity <= 0f
                && IsFinite(settings.GroundHeight)
                && IsFinite(settings.MinimumX)
                && IsFinite(settings.MaximumX)
                && IsFinite(settings.MinimumZ)
                && IsFinite(settings.MaximumZ)
                && settings.MinimumX < settings.MaximumX
                && settings.MinimumZ < settings.MaximumZ
                && IsFinite(settings.CharacterRadius)
                && settings.CharacterRadius > 0f
                && IsFinite(settings.CharacterHeight)
                && settings.CharacterHeight >= settings.CharacterRadius * 2f
                && IsFinite(settings.StepHeight)
                && settings.StepHeight >= 0f
                && settings.StepHeight < settings.CharacterHeight
                && IsFinite(settings.MaximumSlopeDegrees)
                && settings.MaximumSlopeDegrees > 0f
                && settings.MaximumSlopeDegrees < 89f
                && IsFinite(settings.GroundSnapDistance)
                && settings.GroundSnapDistance >= 0f
                && IsFinite(settings.MaximumSubstepDistance)
                && settings.MaximumSubstepDistance > 0f
                && settings.MaximumSubstepDistance <= settings.CharacterRadius
                && settings.MaximumPenetrationIterations > 0
                && settings.MaximumPenetrationIterations <= 16;
        }

        private static bool IsValidPlayerState(RealtimePlayerState state)
        {
            return state != null
                && IsFinite(state.PositionX)
                && IsFinite(state.PositionY)
                && IsFinite(state.PositionZ)
                && IsFinite(state.VelocityX)
                && IsFinite(state.VelocityY)
                && IsFinite(state.VelocityZ)
                && IsFinite(state.YawDegrees);
        }

        private static bool IsUnitInput(float moveX, float moveY)
        {
            if (!IsFinite(moveX) || !IsFinite(moveY))
            {
                return false;
            }

            return (moveX * moveX) + (moveY * moveY) <= 1.0001f;
        }

        private static bool AreValidMovementButtons(RealtimeMovementButtons buttons)
        {
            const RealtimeMovementButtons all = RealtimeMovementButtons.Sprint
                | RealtimeMovementButtons.Jump
                | RealtimeMovementButtons.Aim;
            return (buttons & ~all) == 0;
        }
    }
}
