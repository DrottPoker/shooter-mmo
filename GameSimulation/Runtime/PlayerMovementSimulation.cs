using System;

namespace ShooterMmo.GameSimulation
{
    [Flags]
    public enum PlayerMovementButtons : byte
    {
        None = 0,
        Sprint = 1,
        Jump = 2,
        Aim = 4
    }

    public sealed class MovementSimulationSettings
    {
        public MovementSimulationSettings(
            int tickRateHz,
            float walkSpeed,
            float sprintSpeed,
            float rotationSpeedDegrees,
            float gravity,
            float jumpVelocity,
            float groundedVerticalVelocity,
            float groundHeight,
            float minimumX,
            float maximumX,
            float minimumZ,
            float maximumZ)
            : this(
                tickRateHz,
                walkSpeed,
                sprintSpeed,
                rotationSpeedDegrees,
                gravity,
                55f,
                jumpVelocity,
                groundedVerticalVelocity,
                groundHeight,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ,
                CharacterCollisionSettings.Default)
        {
        }

        public MovementSimulationSettings(
            int tickRateHz,
            float walkSpeed,
            float sprintSpeed,
            float rotationSpeedDegrees,
            float gravity,
            float jumpVelocity,
            float groundedVerticalVelocity,
            float groundHeight,
            float minimumX,
            float maximumX,
            float minimumZ,
            float maximumZ,
            CharacterCollisionSettings characterCollision)
            : this(
                tickRateHz,
                walkSpeed,
                sprintSpeed,
                rotationSpeedDegrees,
                gravity,
                55f,
                jumpVelocity,
                groundedVerticalVelocity,
                groundHeight,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ,
                characterCollision)
        {
        }

        public MovementSimulationSettings(
            int tickRateHz,
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
            CharacterCollisionSettings characterCollision)
        {
            if (tickRateHz <= 0 || tickRateHz > 120)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRateHz));
            }

            if (!IsFinite(walkSpeed) || walkSpeed <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(walkSpeed));
            }

            if (!IsFinite(sprintSpeed) || sprintSpeed < walkSpeed)
            {
                throw new ArgumentOutOfRangeException(nameof(sprintSpeed));
            }

            if (!IsFinite(rotationSpeedDegrees) || rotationSpeedDegrees <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(rotationSpeedDegrees));
            }

            if (!IsFinite(gravity) || gravity >= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(gravity));
            }

            if (!IsFinite(maximumFallSpeed) || maximumFallSpeed <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumFallSpeed));
            }

            if (!IsFinite(jumpVelocity) || jumpVelocity <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(jumpVelocity));
            }

            if (!IsFinite(groundedVerticalVelocity) || groundedVerticalVelocity > 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(groundedVerticalVelocity));
            }

            if (!IsFinite(groundHeight)
                || !IsFinite(minimumX)
                || !IsFinite(maximumX)
                || !IsFinite(minimumZ)
                || !IsFinite(maximumZ)
                || minimumX >= maximumX
                || minimumZ >= maximumZ)
            {
                throw new ArgumentException("World movement bounds are invalid.");
            }

            CharacterCollision = characterCollision
                ?? throw new ArgumentNullException(nameof(characterCollision));
            var maximumPerTickDisplacement = Math.Max(
                sprintSpeed,
                Math.Max(maximumFallSpeed, jumpVelocity)) / tickRateHz;
            var requiredSubsteps = Math.Ceiling(
                (double)maximumPerTickDisplacement
                / CharacterCollision.MaximumSubstepDistance);
            if (requiredSubsteps > KinematicCharacterMotor.MaximumSubstepsPerMove)
            {
                throw new ArgumentException(
                    "Movement settings exceed the collision substep budget.");
            }

            TickRateHz = tickRateHz;
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
        }

        public int TickRateHz { get; }

        public float FixedDeltaTime
        {
            get { return 1f / TickRateHz; }
        }

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

        public CharacterCollisionSettings CharacterCollision { get; }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public readonly struct PlayerMovementInput
    {
        public PlayerMovementInput(
            uint inputSequence,
            uint clientTick,
            float moveX,
            float moveY,
            float cameraYawDegrees,
            PlayerMovementButtons buttons)
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

        public PlayerMovementButtons Buttons { get; }

        public bool SprintHeld
        {
            get { return (Buttons & PlayerMovementButtons.Sprint) != 0; }
        }

        public bool JumpPressed
        {
            get { return (Buttons & PlayerMovementButtons.Jump) != 0; }
        }

        public bool AimHeld
        {
            get { return (Buttons & PlayerMovementButtons.Aim) != 0; }
        }
    }

    public readonly struct PlayerMovementState
    {
        public PlayerMovementState(
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

    public static class MovementSequence
    {
        public static bool IsNewer(uint candidate, uint baseline)
        {
            return candidate != baseline && unchecked(candidate - baseline) < 0x80000000u;
        }
    }

    public static class PlayerMovementSimulation
    {
        public static PlayerMovementState CreateInitialState(
            MovementSimulationSettings settings,
            ICollisionWorld collisionWorld,
            float positionX,
            float positionY,
            float positionZ,
            float yawDegrees)
        {
            if (collisionWorld == null)
            {
                throw new ArgumentNullException(nameof(collisionWorld));
            }

            var position = new SimulationVector3(
                Clamp(positionX, settings.MinimumX, settings.MaximumX),
                positionY,
                Clamp(positionZ, settings.MinimumZ, settings.MaximumZ));
            var collision = settings.CharacterCollision;
            var queryBuffer = new CollisionQueryBuffer();
            var isGrounded = KinematicCharacterMotor.TryFindWalkableGround(
                collisionWorld,
                collision,
                position,
                collision.StepHeight,
                collision.GroundSnapDistance,
                queryBuffer,
                out var supportHeight,
                out _);
            if (isGrounded)
            {
                position = new SimulationVector3(position.X, supportHeight, position.Z);
            }

            return new PlayerMovementState(
                position.X,
                position.Y,
                position.Z,
                0f,
                isGrounded ? settings.GroundedVerticalVelocity : 0f,
                0f,
                NormalizeAngle(yawDegrees),
                isGrounded,
                false);
        }

        public static PlayerMovementState Step(
            PlayerMovementState state,
            PlayerMovementInput input,
            MovementSimulationSettings settings,
            PlayerCarryState carryState,
            ICollisionWorld collisionWorld)
        {
            return Step(
                state,
                input,
                settings,
                carryState,
                collisionWorld,
                new CollisionQueryBuffer());
        }

        public static PlayerMovementState Step(
            PlayerMovementState state,
            PlayerMovementInput input,
            MovementSimulationSettings settings,
            PlayerCarryState carryState,
            ICollisionWorld collisionWorld,
            CollisionQueryBuffer queryBuffer)
        {
            if (carryState == null)
            {
                throw new ArgumentNullException(nameof(carryState));
            }

            if (collisionWorld == null)
            {
                throw new ArgumentNullException(nameof(collisionWorld));
            }

            if (queryBuffer == null)
            {
                throw new ArgumentNullException(nameof(queryBuffer));
            }

            CalculatePlanarMovement(
                state,
                input,
                out var directionX,
                out var directionZ,
                out var directionLength,
                out var cameraYaw);

            var isGrounded = state.IsGrounded;
            var acceptsPlanarControl = isGrounded;
            var isSprinting = state.IsSprinting;
            if (!carryState.SprintAllowed || input.AimHeld || !input.SprintHeld)
            {
                isSprinting = false;
            }
            else if (!isSprinting && isGrounded)
            {
                isSprinting = true;
            }

            var speed = (isSprinting ? settings.SprintSpeed : settings.WalkSpeed)
                * carryState.MovementMultiplier;
            var velocity = acceptsPlanarControl
                ? new SimulationVector3(
                    directionX * speed,
                    state.VelocityY,
                    directionZ * speed)
                : new SimulationVector3(
                    state.VelocityX,
                    state.VelocityY,
                    state.VelocityZ);
            if (isGrounded && velocity.Y < 0f)
            {
                velocity = new SimulationVector3(
                    velocity.X,
                    settings.GroundedVerticalVelocity,
                    velocity.Z);
            }

            if (input.JumpPressed && isGrounded && !input.AimHeld)
            {
                velocity = new SimulationVector3(
                    velocity.X,
                    settings.JumpVelocity,
                    velocity.Z);
                isGrounded = false;
            }

            velocity = new SimulationVector3(
                velocity.X,
                Math.Max(
                    velocity.Y + (settings.Gravity * settings.FixedDeltaTime),
                    -settings.MaximumFallSpeed),
                velocity.Z);
            var movement = KinematicCharacterMotor.Move(
                collisionWorld,
                settings.CharacterCollision,
                new SimulationVector3(state.PositionX, state.PositionY, state.PositionZ),
                velocity * settings.FixedDeltaTime,
                velocity,
                isGrounded,
                queryBuffer);

            var positionX = Clamp(movement.Position.X, settings.MinimumX, settings.MaximumX);
            var positionZ = Clamp(movement.Position.Z, settings.MinimumZ, settings.MaximumZ);
            var velocityY = movement.IsGrounded
                ? settings.GroundedVerticalVelocity
                : movement.Velocity.Y;
            var targetYaw = state.YawDegrees;
            if (acceptsPlanarControl && input.AimHeld)
            {
                targetYaw = cameraYaw;
            }
            else if (acceptsPlanarControl && directionLength > 0.001f)
            {
                targetYaw = NormalizeAngle(
                    (float)(Math.Atan2(directionX, directionZ) * (180d / Math.PI)));
            }

            var yaw = MoveAngleTowards(
                state.YawDegrees,
                targetYaw,
                settings.RotationSpeedDegrees * settings.FixedDeltaTime);
            return new PlayerMovementState(
                positionX,
                movement.Position.Y,
                positionZ,
                movement.Velocity.X,
                velocityY,
                movement.Velocity.Z,
                yaw,
                movement.IsGrounded,
                isSprinting);
        }

        private static void CalculatePlanarMovement(
            PlayerMovementState state,
            PlayerMovementInput input,
            out float directionX,
            out float directionZ,
            out float directionLength,
            out float cameraYaw)
        {
            var moveX = IsFinite(input.MoveX) ? Clamp(input.MoveX, -1f, 1f) : 0f;
            var moveY = IsFinite(input.MoveY) ? Clamp(input.MoveY, -1f, 1f) : 0f;
            var moveLength = (float)Math.Sqrt((moveX * moveX) + (moveY * moveY));
            if (moveLength > 1f)
            {
                moveX /= moveLength;
                moveY /= moveLength;
            }

            cameraYaw = IsFinite(input.CameraYawDegrees)
                ? NormalizeAngle(input.CameraYawDegrees)
                : state.YawDegrees;
            var radians = cameraYaw * ((float)Math.PI / 180f);
            var forwardX = (float)Math.Sin(radians);
            var forwardZ = (float)Math.Cos(radians);
            var rightX = forwardZ;
            var rightZ = -forwardX;
            directionX = (rightX * moveX) + (forwardX * moveY);
            directionZ = (rightZ * moveX) + (forwardZ * moveY);
            directionLength = (float)Math.Sqrt(
                (directionX * directionX) + (directionZ * directionZ));
            if (directionLength > 1f)
            {
                directionX /= directionLength;
                directionZ /= directionLength;
                directionLength = 1f;
            }
        }

        private static float MoveAngleTowards(float current, float target, float maximumDelta)
        {
            var delta = DeltaAngle(current, target);
            if (Math.Abs(delta) <= maximumDelta)
            {
                return NormalizeAngle(target);
            }

            return NormalizeAngle(current + (Math.Sign(delta) * maximumDelta));
        }

        private static float DeltaAngle(float current, float target)
        {
            var delta = NormalizeAngle(target) - NormalizeAngle(current);
            if (delta > 180f)
            {
                delta -= 360f;
            }
            else if (delta < -180f)
            {
                delta += 360f;
            }

            return delta;
        }

        private static float NormalizeAngle(float angle)
        {
            var normalized = angle % 360f;
            return normalized < 0f ? normalized + 360f : normalized;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
