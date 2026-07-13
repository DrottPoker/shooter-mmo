using System;

namespace ShooterMmo.GameSimulation
{
    public sealed class CharacterCollisionSettings
    {
        public CharacterCollisionSettings(
            float radius,
            float height,
            float stepHeight,
            float maximumSlopeDegrees,
            float groundSnapDistance,
            float maximumSubstepDistance,
            int maximumPenetrationIterations)
        {
            if (!IsFinite(radius) || radius <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius));
            }

            if (!IsFinite(height) || height < radius * 2f)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            if (!IsFinite(stepHeight) || stepHeight < 0f || stepHeight >= height)
            {
                throw new ArgumentOutOfRangeException(nameof(stepHeight));
            }

            if (!IsFinite(maximumSlopeDegrees)
                || maximumSlopeDegrees <= 0f
                || maximumSlopeDegrees >= 89f)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSlopeDegrees));
            }

            if (!IsFinite(groundSnapDistance) || groundSnapDistance < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(groundSnapDistance));
            }

            if (!IsFinite(maximumSubstepDistance)
                || maximumSubstepDistance <= 0f
                || maximumSubstepDistance > radius)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSubstepDistance));
            }

            if (maximumPenetrationIterations <= 0 || maximumPenetrationIterations > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPenetrationIterations));
            }

            Radius = radius;
            Height = height;
            StepHeight = stepHeight;
            MaximumSlopeDegrees = maximumSlopeDegrees;
            MinimumGroundNormalY = (float)Math.Cos(maximumSlopeDegrees * (Math.PI / 180d));
            GroundSnapDistance = groundSnapDistance;
            MaximumSubstepDistance = maximumSubstepDistance;
            MaximumPenetrationIterations = maximumPenetrationIterations;
        }

        public float Radius { get; }

        public float Height { get; }

        public float StepHeight { get; }

        public float MaximumSlopeDegrees { get; }

        public float MinimumGroundNormalY { get; }

        public float GroundSnapDistance { get; }

        public float MaximumSubstepDistance { get; }

        public int MaximumPenetrationIterations { get; }

        public static CharacterCollisionSettings Default
        {
            get
            {
                return new CharacterCollisionSettings(
                    0.35f,
                    2f,
                    0.35f,
                    45f,
                    0.4f,
                    0.1f,
                    6);
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public readonly struct KinematicCharacterMoveResult
    {
        public KinematicCharacterMoveResult(
            SimulationVector3 position,
            SimulationVector3 velocity,
            bool isGrounded,
            SimulationVector3 groundNormal)
        {
            Position = position;
            Velocity = velocity;
            IsGrounded = isGrounded;
            GroundNormal = groundNormal;
        }

        public SimulationVector3 Position { get; }

        public SimulationVector3 Velocity { get; }

        public bool IsGrounded { get; }

        public SimulationVector3 GroundNormal { get; }
    }

    public static class KinematicCharacterMotor
    {
        public const int MaximumSubstepsPerMove = 256;
        private const float ContactEpsilon = 0.0001f;

        public static KinematicCharacterMoveResult Move(
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings settings,
            SimulationVector3 position,
            SimulationVector3 displacement,
            SimulationVector3 velocity,
            bool wasGrounded)
        {
            if (collisionWorld == null)
            {
                throw new ArgumentNullException(nameof(collisionWorld));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (!SimulationVector3.IsFinite(position)
                || !SimulationVector3.IsFinite(displacement)
                || !SimulationVector3.IsFinite(velocity))
            {
                throw new ArgumentException("Character movement vectors must be finite.");
            }

            var maximumComponent = Math.Max(
                Math.Abs(displacement.X),
                Math.Max(Math.Abs(displacement.Y), Math.Abs(displacement.Z)));
            var requiredSubsteps = Math.Max(
                1d,
                Math.Ceiling((double)maximumComponent / settings.MaximumSubstepDistance));
            if (requiredSubsteps > MaximumSubstepsPerMove)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(displacement),
                    "Character displacement exceeds the collision substep budget.");
            }

            var substepCount = (int)requiredSubsteps;
            var substep = displacement / substepCount;
            var current = position;
            var currentVelocity = velocity;
            var grounded = wasGrounded;
            var groundNormal = SimulationVector3.UnitY;
            var queryBuffer = new CollisionQueryBuffer();

            for (var substepIndex = 0; substepIndex < substepCount; substepIndex++)
            {
                var previous = current;
                var candidate = current + substep;

                if (grounded && substep.Y <= 0f)
                {
                    var horizontalCandidate = new SimulationVector3(candidate.X, current.Y, candidate.Z);
                    if (TryFindWalkableGround(
                        collisionWorld,
                        settings,
                        horizontalCandidate,
                        settings.StepHeight,
                        settings.GroundSnapDistance,
                        queryBuffer,
                        out var supportHeight,
                        out var supportNormal))
                    {
                        horizontalCandidate = new SimulationVector3(
                            horizontalCandidate.X,
                            supportHeight,
                            horizontalCandidate.Z);
                        candidate = horizontalCandidate;
                        groundNormal = supportNormal;
                    }
                    else
                    {
                        grounded = false;
                    }
                }

                var desiredCandidate = candidate;

                var contactGrounded = ResolvePenetrations(
                    collisionWorld,
                    settings,
                    queryBuffer,
                    ref candidate,
                    ref currentVelocity,
                    out var contactGroundNormal);
                if (contactGrounded)
                {
                    grounded = true;
                    groundNormal = contactGroundNormal;
                }

                if (grounded
                    && settings.StepHeight > 0f
                    && HorizontalDistanceSquared(candidate, desiredCandidate)
                        > ContactEpsilon * ContactEpsilon
                    && TryStepUp(
                        collisionWorld,
                        settings,
                        queryBuffer,
                        previous,
                        desiredCandidate,
                        currentVelocity,
                        out var steppedPosition,
                        out var steppedVelocity,
                        out var steppedGroundNormal))
                {
                    candidate = steppedPosition;
                    currentVelocity = steppedVelocity;
                    groundNormal = steppedGroundNormal;
                    grounded = true;
                }

                if (!grounded && currentVelocity.Y <= 0f)
                {
                    var maximumRise = Math.Max(0f, previous.Y - candidate.Y) + ContactEpsilon;
                    if (TryFindWalkableGround(
                            collisionWorld,
                            settings,
                            candidate,
                            maximumRise,
                            settings.GroundSnapDistance + Math.Abs(substep.Y) + ContactEpsilon,
                            queryBuffer,
                            out var landingHeight,
                            out var landingNormal)
                        && candidate.Y <= landingHeight + ContactEpsilon
                        && landingHeight <= previous.Y + ContactEpsilon)
                    {
                        candidate = new SimulationVector3(candidate.X, landingHeight, candidate.Z);
                        currentVelocity = new SimulationVector3(
                            currentVelocity.X,
                            Math.Min(0f, currentVelocity.Y),
                            currentVelocity.Z);
                        grounded = true;
                        groundNormal = landingNormal;
                    }
                }

                current = candidate;
            }

            return new KinematicCharacterMoveResult(
                current,
                currentVelocity,
                grounded,
                grounded ? groundNormal : SimulationVector3.Zero);
        }

        public static bool TryFindWalkableGround(
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings settings,
            SimulationVector3 rootPosition,
            float maximumRise,
            float maximumDrop,
            CollisionQueryBuffer queryBuffer,
            out float groundHeight,
            out SimulationVector3 groundNormal)
        {
            if (!TryFindWalkableGroundSurface(
                    collisionWorld,
                    settings,
                    rootPosition,
                    maximumRise,
                    maximumDrop,
                    queryBuffer,
                    out var surfaceHeight,
                    out groundNormal))
            {
                groundHeight = 0f;
                return false;
            }

            groundHeight = CalculateCapsuleSupportHeight(
                surfaceHeight,
                groundNormal,
                settings.Radius);
            return true;
        }

        public static bool TryFindWalkableGroundSurface(
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings settings,
            SimulationVector3 rootPosition,
            float maximumRise,
            float maximumDrop,
            CollisionQueryBuffer queryBuffer,
            out float surfaceHeight,
            out SimulationVector3 groundNormal)
        {
            surfaceHeight = 0f;
            groundNormal = SimulationVector3.Zero;
            var origin = new SimulationVector3(
                rootPosition.X,
                rootPosition.Y + maximumRise + ContactEpsilon,
                rootPosition.Z);
            var distance = maximumRise + maximumDrop + (ContactEpsilon * 2f);
            var rayBounds = new CollisionAabb(
                new SimulationVector3(origin.X, origin.Y - distance, origin.Z),
                origin).Expanded(ContactEpsilon);
            queryBuffer.Clear();
            collisionWorld.QueryBoxes(rayBounds, CollisionLayers.CharacterMovement, queryBuffer);
            queryBuffer.SortByStableId();

            var found = false;
            var highest = float.MinValue;
            for (var index = 0; index < queryBuffer.Boxes.Count; index++)
            {
                if (!CollisionMath.TryRaycastBox(
                        origin,
                        new SimulationVector3(0f, -1f, 0f),
                        distance,
                        queryBuffer.Boxes[index],
                        out var hit)
                    || hit.Normal.Y < settings.MinimumGroundNormalY
                    || hit.Point.Y > rootPosition.Y + maximumRise + ContactEpsilon
                    || hit.Point.Y < rootPosition.Y - maximumDrop - ContactEpsilon
                    || hit.Point.Y <= highest)
                {
                    continue;
                }

                highest = hit.Point.Y;
                groundNormal = hit.Normal;
                found = true;
            }

            surfaceHeight = highest;
            return found;
        }

        public static float CalculateCapsuleSupportHeight(
            float surfaceHeight,
            SimulationVector3 groundNormal,
            float capsuleRadius)
        {
            if (!IsFinite(surfaceHeight))
            {
                throw new ArgumentOutOfRangeException(nameof(surfaceHeight));
            }

            if (!SimulationVector3.IsFinite(groundNormal) || groundNormal.Y <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(groundNormal));
            }

            if (!IsFinite(capsuleRadius) || capsuleRadius <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(capsuleRadius));
            }

            return surfaceHeight
                + (capsuleRadius * ((1f / groundNormal.Y) - 1f));
        }

        private static bool TryStepUp(
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings settings,
            CollisionQueryBuffer queryBuffer,
            SimulationVector3 previous,
            SimulationVector3 desired,
            SimulationVector3 velocity,
            out SimulationVector3 steppedPosition,
            out SimulationVector3 steppedVelocity,
            out SimulationVector3 groundNormal)
        {
            steppedPosition = desired;
            steppedVelocity = velocity;
            groundNormal = SimulationVector3.Zero;
            var horizontal = new SimulationVector3(
                desired.X - previous.X,
                0f,
                desired.Z - previous.Z);
            var horizontalLength = horizontal.Length;
            if (horizontalLength <= ContactEpsilon)
            {
                return false;
            }

            var direction = horizontal / horizontalLength;
            var elevated = new SimulationVector3(
                desired.X,
                previous.Y + settings.StepHeight + ContactEpsilon,
                desired.Z);
            var elevatedVelocity = velocity;
            ResolvePenetrations(
                collisionWorld,
                settings,
                queryBuffer,
                ref elevated,
                ref elevatedVelocity,
                out _);
            if (HorizontalDistanceSquared(elevated, desired) > ContactEpsilon * ContactEpsilon)
            {
                return false;
            }

            var leadingProbe = new SimulationVector3(
                elevated.X + (direction.X * settings.Radius),
                elevated.Y,
                elevated.Z + (direction.Z * settings.Radius));
            if (!TryFindWalkableGround(
                    collisionWorld,
                    settings,
                    leadingProbe,
                    ContactEpsilon,
                    settings.StepHeight + settings.GroundSnapDistance + ContactEpsilon,
                    queryBuffer,
                    out var supportHeight,
                    out var supportNormal)
                || supportHeight < previous.Y - ContactEpsilon
                || supportHeight > previous.Y + settings.StepHeight + ContactEpsilon)
            {
                return false;
            }

            steppedPosition = new SimulationVector3(elevated.X, supportHeight, elevated.Z);
            steppedVelocity = elevatedVelocity;
            groundNormal = supportNormal;
            return true;
        }

        private static bool ResolvePenetrations(
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings settings,
            CollisionQueryBuffer queryBuffer,
            ref SimulationVector3 position,
            ref SimulationVector3 velocity,
            out SimulationVector3 groundNormal)
        {
            var grounded = false;
            groundNormal = SimulationVector3.Zero;

            for (var iteration = 0; iteration < settings.MaximumPenetrationIterations; iteration++)
            {
                var bounds = CapsuleBounds(position, settings);
                queryBuffer.Clear();
                collisionWorld.QueryBoxes(bounds, CollisionLayers.CharacterMovement, queryBuffer);
                queryBuffer.SortByStableId();
                var resolvedAny = false;

                for (var index = 0; index < queryBuffer.Boxes.Count; index++)
                {
                    if (!CollisionMath.TryGetCapsuleBoxPenetration(
                            position,
                            settings.Radius,
                            settings.Height,
                            queryBuffer.Boxes[index],
                            out var normal,
                            out var depth))
                    {
                        continue;
                    }

                    if (normal.Y >= settings.MinimumGroundNormalY)
                    {
                        position += normal * depth;
                        if (velocity.Y < 0f)
                        {
                            velocity = new SimulationVector3(velocity.X, 0f, velocity.Z);
                        }

                        grounded = true;
                        groundNormal = normal;
                    }
                    else if (normal.Y <= -0.25f)
                    {
                        position += normal * depth;
                        if (velocity.Y > 0f)
                        {
                            velocity = new SimulationVector3(velocity.X, 0f, velocity.Z);
                        }
                    }
                    else
                    {
                        var horizontal = new SimulationVector3(normal.X, 0f, normal.Z);
                        var horizontalLength = horizontal.Length;
                        if (horizontalLength > ContactEpsilon)
                        {
                            var horizontalNormal = horizontal / horizontalLength;
                            position += horizontalNormal * (depth / horizontalLength);
                            var inwardVelocity = SimulationVector3.Dot(velocity, horizontalNormal);
                            if (inwardVelocity < 0f)
                            {
                                velocity -= horizontalNormal * inwardVelocity;
                            }
                        }
                        else
                        {
                            position += normal * depth;
                        }
                    }

                    resolvedAny = true;
                }

                if (!resolvedAny)
                {
                    break;
                }
            }

            return grounded;
        }

        private static CollisionAabb CapsuleBounds(
            SimulationVector3 rootPosition,
            CharacterCollisionSettings settings)
        {
            return new CollisionAabb(
                new SimulationVector3(
                    rootPosition.X - settings.Radius,
                    rootPosition.Y,
                    rootPosition.Z - settings.Radius),
                new SimulationVector3(
                    rootPosition.X + settings.Radius,
                    rootPosition.Y + settings.Height,
                    rootPosition.Z + settings.Radius));
        }

        private static float HorizontalDistanceSquared(
            SimulationVector3 left,
            SimulationVector3 right)
        {
            var x = left.X - right.X;
            var z = left.Z - right.Z;
            return (x * x) + (z * z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
