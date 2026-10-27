using System;

namespace Mmorpg.GameSimulation
{
    public sealed class MovementSimulationSettings
    {
        public MovementSimulationSettings(
            int tickRateHz,
            float walkSpeed,
            float rotationSpeedDegrees,
            float gravity,
            float groundedVerticalVelocity,
            float groundHeight,
            float minimumX,
            float maximumX,
            float minimumZ,
            float maximumZ)
            : this(
                tickRateHz,
                walkSpeed,
                rotationSpeedDegrees,
                gravity,
                55f,
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
            float rotationSpeedDegrees,
            float gravity,
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
                rotationSpeedDegrees,
                gravity,
                55f,
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
            float rotationSpeedDegrees,
            float gravity,
            float maximumFallSpeed,
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
            // The motor counts substeps from the full displacement length, so the budget
            // covers walking and the fastest fall at the same time.
            var maximumPerTickDisplacement = Math.Sqrt(
                ((double)walkSpeed * walkSpeed)
                + ((double)maximumFallSpeed * maximumFallSpeed)) / tickRateHz;
            var requiredSubsteps = Math.Ceiling(
                maximumPerTickDisplacement
                / CharacterCollision.MaximumSubstepDistance);
            if (requiredSubsteps > KinematicCharacterMotor.MaximumSubstepsPerMove)
            {
                throw new ArgumentException(
                    "Movement settings exceed the collision substep budget.");
            }

            TickRateHz = tickRateHz;
            WalkSpeed = walkSpeed;
            RotationSpeedDegrees = rotationSpeedDegrees;
            Gravity = gravity;
            MaximumFallSpeed = maximumFallSpeed;
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
            get { return (float)(1f / TickRateHz); }
        }

        // A path ends after the motor blocked it for half a second.
        public int BlockedPathTickLimit
        {
            get { return (TickRateHz + 1) / 2; }
        }

        public float WalkSpeed { get; }

        public float RotationSpeedDegrees { get; }

        public float Gravity { get; }

        public float MaximumFallSpeed { get; }

        public float GroundedVerticalVelocity { get; }

        public float GroundHeight { get; }

        public float MinimumX { get; }

        public float MaximumX { get; }

        public float MinimumZ { get; }

        public float MaximumZ { get; }

        public CharacterCollisionSettings CharacterCollision { get; }

        // The same settings with the walk speed at percent of this one, such as 300 for three
        // times as fast. The worker and the client compute the same float from the same percent.
        public MovementSimulationSettings WithWalkSpeedPercent(int percent)
        {
            if (percent <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(percent));
            }

            if (percent == 100)
            {
                return this;
            }

            return new MovementSimulationSettings(
                TickRateHz,
                (float)((float)(WalkSpeed * percent) / 100f),
                RotationSpeedDegrees,
                Gravity,
                MaximumFallSpeed,
                GroundedVerticalVelocity,
                GroundHeight,
                MinimumX,
                MaximumX,
                MinimumZ,
                MaximumZ,
                CharacterCollision);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    // One tick of movement intent: the path the player wants to follow, or none to stand.
    public readonly struct PlayerMovementInput
    {
        public PlayerMovementInput(uint inputSequence, uint clientTick, MovementPath path)
        {
            InputSequence = inputSequence;
            ClientTick = clientTick;
            Path = path;
        }

        public uint InputSequence { get; }

        public uint ClientTick { get; }

        public MovementPath Path { get; }

        public uint PathId
        {
            get { return Path != null ? Path.Id : 0u; }
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
            bool isGrounded)
            : this(
                positionX,
                positionY,
                positionZ,
                velocityX,
                velocityY,
                velocityZ,
                yawDegrees,
                isGrounded,
                MovementPathProgress.None)
        {
        }

        public PlayerMovementState(
            float positionX,
            float positionY,
            float positionZ,
            float velocityX,
            float velocityY,
            float velocityZ,
            float yawDegrees,
            bool isGrounded,
            MovementPathProgress pathProgress)
        {
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            VelocityX = velocityX;
            VelocityY = velocityY;
            VelocityZ = velocityZ;
            YawDegrees = yawDegrees;
            IsGrounded = isGrounded;
            PathProgress = pathProgress;
        }

        public float PositionX { get; }

        public float PositionY { get; }

        public float PositionZ { get; }

        public float VelocityX { get; }

        public float VelocityY { get; }

        public float VelocityZ { get; }

        public float YawDegrees { get; }

        public bool IsGrounded { get; }

        // Only the owner snapshot carries progress. Remote states always have none.
        public MovementPathProgress PathProgress { get; }

        public SimulationVector3 Position
        {
            get { return new SimulationVector3(PositionX, PositionY, PositionZ); }
        }

        public PlayerMovementState WithPathProgress(MovementPathProgress pathProgress)
        {
            return new PlayerMovementState(
                PositionX,
                PositionY,
                PositionZ,
                VelocityX,
                VelocityY,
                VelocityZ,
                YawDegrees,
                IsGrounded,
                pathProgress);
        }
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
        // A waypoint counts as reached within one centimeter.
        public const float WaypointArrivalDistance = 0.01f;

        // A tick blocks the path when the motor moved less than this share of the step.
        private const float BlockedProgressRatio = 0.25f;

        private const float WaypointArrivalDistanceSquared =
            WaypointArrivalDistance * WaypointArrivalDistance;

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
                isGrounded);
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

            // A different path in the input replaces the followed one from its start.
            var path = input.Path;
            var progress = state.PathProgress;
            if (progress.PathId != input.PathId)
            {
                progress = new MovementPathProgress(input.PathId, 0, 0);
            }

            var waypointIndex = progress.NextWaypointIndex;
            var blockedTicks = progress.BlockedTicks;
            var isGrounded = state.IsGrounded;
            var startX = state.PositionX;
            var startZ = state.PositionZ;
            var followsPath = false;
            var directionX = 0f;
            var directionZ = 0f;
            var planarSpeed = 0f;
            if (isGrounded && path != null)
            {
                while (waypointIndex < path.Count
                    && PlanarDistanceSquared(startX, startZ, path[waypointIndex])
                        <= WaypointArrivalDistanceSquared)
                {
                    waypointIndex++;
                }

                if (waypointIndex < path.Count)
                {
                    var target = path[waypointIndex];
                    var deltaX = (float)(target.X - startX);
                    var deltaZ = (float)(target.Z - startZ);
                    var distance = (float)Math.Sqrt(
                        (float)((float)(deltaX * deltaX) + (float)(deltaZ * deltaZ)));
                    directionX = (float)(deltaX / distance);
                    directionZ = (float)(deltaZ / distance);
                    var speed = (float)(settings.WalkSpeed * carryState.MovementMultiplier);
                    var stepDistance = (float)(speed * settings.FixedDeltaTime);
                    // The last step lands exactly on the waypoint instead of passing it.
                    planarSpeed = distance < stepDistance
                        ? (float)(distance / settings.FixedDeltaTime)
                        : speed;
                    followsPath = true;
                }
            }

            // Grounded characters move only along the path. Airborne characters keep
            // their momentum until they land.
            var velocity = isGrounded
                ? new SimulationVector3(
                    (float)(directionX * planarSpeed),
                    state.VelocityY,
                    (float)(directionZ * planarSpeed))
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

            var gravityStep = (float)(settings.Gravity * settings.FixedDeltaTime);
            velocity = new SimulationVector3(
                velocity.X,
                Math.Max(
                    (float)(velocity.Y + gravityStep),
                    -settings.MaximumFallSpeed),
                velocity.Z);
            var displacement = velocity * settings.FixedDeltaTime;
            var movement = KinematicCharacterMotor.Move(
                collisionWorld,
                settings.CharacterCollision,
                new SimulationVector3(state.PositionX, state.PositionY, state.PositionZ),
                displacement,
                velocity,
                isGrounded,
                queryBuffer);

            var positionX = Clamp(movement.Position.X, settings.MinimumX, settings.MaximumX);
            var positionZ = Clamp(movement.Position.Z, settings.MinimumZ, settings.MaximumZ);
            var velocityY = movement.IsGrounded
                ? settings.GroundedVerticalVelocity
                : movement.Velocity.Y;

            if (followsPath)
            {
                var movedX = (float)(positionX - startX);
                var movedZ = (float)(positionZ - startZ);
                var moved = (float)((float)(movedX * movedX) + (float)(movedZ * movedZ));
                var minimumProgress = (float)((float)(planarSpeed * settings.FixedDeltaTime)
                    * BlockedProgressRatio);
                blockedTicks = moved < (float)(minimumProgress * minimumProgress)
                    ? blockedTicks + 1
                    : 0;
                if (PlanarDistanceSquared(positionX, positionZ, path[waypointIndex])
                    <= WaypointArrivalDistanceSquared)
                {
                    waypointIndex++;
                    blockedTicks = 0;
                }
                else if (blockedTicks >= settings.BlockedPathTickLimit)
                {
                    waypointIndex = path.Count;
                    blockedTicks = 0;
                }
            }
            else if (path == null || waypointIndex >= path.Count)
            {
                blockedTicks = 0;
            }

            var targetYaw = state.YawDegrees;
            if (followsPath)
            {
                targetYaw = NormalizeAngle(
                    (float)(Math.Atan2(directionX, directionZ) * (180d / Math.PI)));
            }

            var yaw = MoveAngleTowards(
                state.YawDegrees,
                targetYaw,
                (float)(settings.RotationSpeedDegrees * settings.FixedDeltaTime));
            return new PlayerMovementState(
                positionX,
                movement.Position.Y,
                positionZ,
                movement.Velocity.X,
                velocityY,
                movement.Velocity.Z,
                yaw,
                movement.IsGrounded,
                new MovementPathProgress(progress.PathId, waypointIndex, blockedTicks));
        }

        private static float PlanarDistanceSquared(float x, float z, SimulationVector3 point)
        {
            var deltaX = (float)(point.X - x);
            var deltaZ = (float)(point.Z - z);
            return (float)((float)(deltaX * deltaX) + (float)(deltaZ * deltaZ));
        }

        private static float MoveAngleTowards(float current, float target, float maximumDelta)
        {
            var delta = DeltaAngle(current, target);
            if (Math.Abs(delta) <= maximumDelta)
            {
                return NormalizeAngle(target);
            }

            return NormalizeAngle((float)(current + (float)(Math.Sign(delta) * maximumDelta)));
        }

        private static float DeltaAngle(float current, float target)
        {
            var delta = (float)(NormalizeAngle(target) - NormalizeAngle(current));
            if (delta > 180f)
            {
                delta = (float)(delta - 360f);
            }
            else if (delta < -180f)
            {
                delta = (float)(delta + 360f);
            }

            return delta;
        }

        private static float NormalizeAngle(float angle)
        {
            var normalized = (float)(angle % 360f);
            return normalized < 0f ? (float)(normalized + 360f) : normalized;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
