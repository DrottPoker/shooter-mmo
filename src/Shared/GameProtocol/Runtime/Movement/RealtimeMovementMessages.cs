namespace Mmorpg.GameProtocol
{
    public sealed class RealtimeMovementSettings
    {
        public RealtimeMovementSettings(
            ushort tickRateHz,
            ushort snapshotRateHz,
            float walkSpeed,
            float sprintSpeed,
            float rotationSpeedDegrees,
            float gravity,
            float maximumFallSpeed,
            float jumpVelocity,
            float groundedVerticalVelocity,
            float groundHeight,
            float minimumX,
            float maximumX,
            float minimumZ,
            float maximumZ,
            float characterRadius,
            float characterHeight,
            float stepHeight,
            float maximumSlopeDegrees,
            float groundSnapDistance,
            float maximumSubstepDistance,
            byte maximumPenetrationIterations)
        {
            TickRateHz = tickRateHz;
            SnapshotRateHz = snapshotRateHz;
            WalkSpeed = walkSpeed;
            SprintSpeed = sprintSpeed;
            RotationSpeedDegrees = rotationSpeedDegrees;
            Gravity = gravity;
            MaximumFallSpeed = maximumFallSpeed;
            JumpVelocity = jumpVelocity;
            GroundedVerticalVelocity = groundedVerticalVelocity;
            GroundHeight = groundHeight;
            MinimumX = minimumX;
            MaximumX = maximumX;
            MinimumZ = minimumZ;
            MaximumZ = maximumZ;
            CharacterRadius = characterRadius;
            CharacterHeight = characterHeight;
            StepHeight = stepHeight;
            MaximumSlopeDegrees = maximumSlopeDegrees;
            GroundSnapDistance = groundSnapDistance;
            MaximumSubstepDistance = maximumSubstepDistance;
            MaximumPenetrationIterations = maximumPenetrationIterations;
        }

        public ushort TickRateHz { get; }

        public ushort SnapshotRateHz { get; }

        public float WalkSpeed { get; }

        public float SprintSpeed { get; }

        public float RotationSpeedDegrees { get; }

        public float Gravity { get; }

        public float MaximumFallSpeed { get; }

        public float JumpVelocity { get; }

        public float GroundedVerticalVelocity { get; }

        public float GroundHeight { get; }

        public float MinimumX { get; }

        public float MaximumX { get; }

        public float MinimumZ { get; }

        public float MaximumZ { get; }

        public float CharacterRadius { get; }

        public float CharacterHeight { get; }

        public float StepHeight { get; }

        public float MaximumSlopeDegrees { get; }

        public float GroundSnapDistance { get; }

        public float MaximumSubstepDistance { get; }

        public byte MaximumPenetrationIterations { get; }
    }

    public sealed class RealtimeMovementInput
    {
        public RealtimeMovementInput(
            uint inputSequence,
            uint clientTick,
            float moveX,
            float moveY,
            float cameraYawDegrees,
            RealtimeMovementButtons buttons)
        {
            InputSequence = inputSequence;
            ClientTick = clientTick;
            MoveX = moveX;
            MoveY = moveY;
            CameraYawDegrees = cameraYawDegrees;
            Buttons = buttons;
        }

        public uint InputSequence { get; }

        public uint ClientTick { get; }

        public float MoveX { get; }

        public float MoveY { get; }

        public float CameraYawDegrees { get; }

        public RealtimeMovementButtons Buttons { get; }
    }

    public sealed class RealtimePlayerState
    {
        public RealtimePlayerState(
            float positionX,
            float positionY,
            float positionZ,
            float velocityX,
            float velocityY,
            float velocityZ,
            float yawDegrees,
            bool isGrounded,
            bool isSprinting)
        {
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            VelocityX = velocityX;
            VelocityY = velocityY;
            VelocityZ = velocityZ;
            YawDegrees = yawDegrees;
            IsGrounded = isGrounded;
            IsSprinting = isSprinting;
        }

        public float PositionX { get; }

        public float PositionY { get; }

        public float PositionZ { get; }

        public float VelocityX { get; }

        public float VelocityY { get; }

        public float VelocityZ { get; }

        public float YawDegrees { get; }

        public bool IsGrounded { get; }

        public bool IsSprinting { get; }
    }

    public sealed class RealtimeOwnerSimulationSnapshot
    {
        public RealtimeOwnerSimulationSnapshot(
            uint snapshotSequence,
            uint serverTick,
            uint lastProcessedInputSequence,
            RealtimePlayerState state)
        {
            SnapshotSequence = snapshotSequence;
            ServerTick = serverTick;
            LastProcessedInputSequence = lastProcessedInputSequence;
            State = state;
        }

        public uint SnapshotSequence { get; }

        public uint ServerTick { get; }

        public uint LastProcessedInputSequence { get; }

        public RealtimePlayerState State { get; }
    }
}
