using Unity.Collections;
using Unity.Mathematics;

namespace Mmorpg.Client.Cloth
{
    // The springs on the target pose, the swing of docs/EQUIPMENT_VISUALS_DESIGN.md: every particle
    // of a chain has a place and a speed in the frame of its anchor bone, the hips for a skirt, the
    // upper chest for a cloak, and the head for a hood, and springs toward where the target pose
    // lays it at a fixed rate, whatever the frame rate. The anchor's motion in the world reaches the
    // cloth as filtered and limited forces of inertia, and gravity only as far as the anchor leans
    // away from the bind pose, so a body in the bind pose wears its garments as they were made.
    // Each step then holds the links to the neighbouring chains, the chains' twist around the
    // anchor's upright axis, the bones' lengths, and a cone around where the target points every
    // bone, limits the speed, and turns no bone nearer the body than the target pose: the legs and
    // the body decide where the cloth is, and the springs only swing it out and to the side. What
    // is shown is laid with the target pose's collision, which leaves the target pose where it is.
    // ClothWorld runs them in its Burst jobs.
    public static class ClothSprings
    {
        // The springs step at this rate, at most this many steps a frame; a longer frame drops the
        // rest of its time, so it plays slower rather than throw the cloth.
        public const float Rate = 90f;
        public const float StepSeconds = 1f / Rate;
        public const int MaxSteps = 4;

        // The anchor's speed and turning are filtered over about this many seconds, and its
        // acceleration again, so the steps of the network and of the prediction do not jolt the
        // cloth.
        public const float FilterSeconds = 0.025f;

        // The cloth feels the anchor's acceleration, turning, and turning's acceleration only up to
        // these, in meters a second squared, degrees a second, and radians a second squared: beyond,
        // as in the spin of an attack, it feels the limit and settles calmly after it.
        public const float MaxAcceleration = 20f;
        public const float MaxTurningDegrees = 540f;
        public const float MaxTurningAcceleration = 60f;

        // The cloth moves at most this fast against its anchor, in meters a second.
        public const float MaxSpeed = 8f;

        // The body's pushes give the cloth at most this speed, in meters a second: it goes where the
        // legs push it, but a jolt of a clip, such as a knee that snaps straight, does not fling it.
        public const float PushSpeed = 0.3f;

        // Neighbouring particles keep between these shares of how far apart the target pose lays
        // them.
        public const float LinkShortest = 0.6f;
        public const float LinkLongest = 1.4f;

        // The share of its speed along the body a particle loses in a step where the collision moved
        // its bone; its speed into the body goes, and it does not bounce.
        public const float Friction = 0.3f;

        // The constraints are held this many times a step.
        public const int Passes = 2;

        // A body whose cloth lay this close to its target, in meters, for this long, in seconds,
        // sleeps on it until a step would move it farther.
        public const float SleepDistance = 0.001f;
        public const float SleepSeconds = 0.5f;

        // The springs fade in and out over this many seconds, always starting from the target.
        public const float FadeSeconds = 0.3f;

        // In a frame where a body moves farther than this many meters, faster than this many meters a
        // second, or turns more than this many degrees, it jumped: its cloth is carried along rigidly.
        public const float JumpDistance = 2f;
        public const float JumpSpeed = 20f;
        public const float JumpDegrees = 150f;

        // Gravity, in meters a second squared.
        public const float Gravity = 9.81f;

        // A particle this near its anchor's upright axis, in meters, has no twist to limit.
        private const float TwistReach = 0.02f;

        // A chain twists toward a neighbour at most this share of the angle between their targets,
        // so two neighbours that twist toward each other never meet, however close the legs push
        // their targets.
        public const float NeighbourTwist = 0.45f;

        // The kinds of anchor a body has: one per kind of chain.
        public const int Anchors = 3;

        // Moves an anchor to where it is this frame and filters its motion: its speed and turning are
        // filtered, the springs feel them change no faster than the limits allow, and that change is
        // filtered again as the acceleration they feel. The first frame only places it, and the
        // second takes its speed and turning as they are, so cloth that starts on a moving body
        // feels no start.
        public static void Move(ref ClothAnchor anchor, float3 position, quaternion rotation, float deltaTime)
        {
            Keep(ref anchor);
            if (anchor.Primed == 0 || deltaTime <= 0f)
            {
                anchor.Position = position;
                anchor.Rotation = rotation;
                anchor.Primed = anchor.Primed == 0 ? (byte)1 : anchor.Primed;
                return;
            }

            var velocity = (position - anchor.Position) / deltaTime;
            var turning = AngularVelocity(anchor.Rotation, rotation, deltaTime);
            anchor.Position = position;
            anchor.Rotation = rotation;
            var maxTurning = math.radians(MaxTurningDegrees);
            if (anchor.Primed == 1)
            {
                anchor.Velocity = velocity;
                anchor.AngularVelocity = turning;
                anchor.FeltVelocity = velocity;
                anchor.FeltAngularVelocity = ClampLength(turning, maxTurning);
                anchor.FeltAcceleration = float3.zero;
                anchor.FeltAngularAcceleration = float3.zero;
                anchor.Primed = 2;
                return;
            }

            var share = 1f - math.exp(-deltaTime / FilterSeconds);
            anchor.Velocity += (velocity - anchor.Velocity) * share;
            anchor.AngularVelocity += (turning - anchor.AngularVelocity) * share;

            var felt = anchor.FeltVelocity + ClampLength(anchor.Velocity - anchor.FeltVelocity, MaxAcceleration * deltaTime);
            anchor.FeltAcceleration += ((felt - anchor.FeltVelocity) / deltaTime - anchor.FeltAcceleration) * share;
            anchor.FeltVelocity = felt;

            var feltTurning = anchor.FeltAngularVelocity
                + ClampLength(ClampLength(anchor.AngularVelocity, maxTurning) - anchor.FeltAngularVelocity, MaxTurningAcceleration * deltaTime);
            anchor.FeltAngularAcceleration += ((feltTurning - anchor.FeltAngularVelocity) / deltaTime - anchor.FeltAngularAcceleration) * share;
            anchor.FeltAngularVelocity = feltTurning;
        }

        // Moves an anchor across a jump: its place is taken as it is, and nothing of the jump
        // reaches the filters, so the cloth feels the motion it felt before, as if the body had not
        // jumped.
        public static void Carry(ref ClothAnchor anchor, float3 position, quaternion rotation)
        {
            Keep(ref anchor);
            anchor.Position = position;
            anchor.Rotation = rotation;
            anchor.Primed = anchor.Primed == 0 ? (byte)1 : anchor.Primed;
        }

        // What the springs felt of an anchor last frame.
        private static void Keep(ref ClothAnchor anchor)
        {
            anchor.LastFeltVelocity = anchor.FeltVelocity;
            anchor.LastFeltAcceleration = anchor.FeltAcceleration;
            anchor.LastFeltAngularVelocity = anchor.FeltAngularVelocity;
            anchor.LastFeltAngularAcceleration = anchor.FeltAngularAcceleration;
        }

        // What the springs feel of an anchor at a share of the way from last frame to this one, so the
        // steps between frames feel its motion change smoothly, whatever the frame time.
        public static ClothAnchor Between(in ClothAnchor anchor, float share)
        {
            var between = anchor;
            between.FeltVelocity = math.lerp(anchor.LastFeltVelocity, anchor.FeltVelocity, share);
            between.FeltAcceleration = math.lerp(anchor.LastFeltAcceleration, anchor.FeltAcceleration, share);
            between.FeltAngularVelocity = math.lerp(anchor.LastFeltAngularVelocity, anchor.FeltAngularVelocity, share);
            between.FeltAngularAcceleration = math.lerp(anchor.LastFeltAngularAcceleration, anchor.FeltAngularAcceleration, share);
            return between;
        }

        // Whether a bone jumped between two frames.
        public static bool Jumped(float3 fromPosition, quaternion fromRotation, float3 toPosition, quaternion toRotation, float deltaTime)
        {
            var distance = math.distance(fromPosition, toPosition);
            return distance > JumpDistance || (deltaTime > 0f && distance > JumpSpeed * deltaTime)
                || Angle(fromRotation, toRotation) > math.radians(JumpDegrees);
        }

        // The angle between two rotations, in radians.
        public static float Angle(quaternion from, quaternion to)
        {
            var dot = math.min(math.abs(math.dot(from.value, to.value)), 1f);
            return 2f * math.acos(dot);
        }

        // The turning from one rotation to another over a time, in radians a second about a world axis.
        public static float3 AngularVelocity(quaternion from, quaternion to, float deltaTime)
        {
            var delta = math.mul(to, math.conjugate(from)).value;
            if (delta.w < 0f)
            {
                delta = -delta;
            }

            var sine = math.length(delta.xyz);
            if (sine < 1e-7f)
            {
                return 2f * delta.xyz / deltaTime;
            }

            return delta.xyz / sine * (2f * math.atan2(sine, delta.w) / deltaTime);
        }

        public static float3 ClampLength(float3 value, float largest)
        {
            var length = math.length(value);
            return length > largest && length > 0f ? value * (largest / length) : value;
        }

        // The acceleration a particle feels in its anchor's frame, which turns by a rotation in the
        // world: gravity as far as the anchor leans from the bind pose, the anchor's acceleration,
        // turning, and turning's acceleration as forces of inertia, and the lean of its speed, as a
        // wind that bends the cloth the more the farther along its chain it hangs, from its root at 0
        // to its tip at 1. The particle lies at a place from the anchor and moves at a speed against
        // it, in its frame.
        public static float3 Felt(in ClothSpring spring, in ClothAnchor anchor, quaternion rotation, float3 up, float3 place, float3 speed,
            float along)
        {
            var inverse = math.conjugate(rotation);
            var down = math.mul(inverse, new float3(0f, -1f, 0f));
            var gravity = spring.Gravity * Gravity * (down + up);
            var turning = math.mul(inverse, anchor.FeltAngularVelocity);
            var turningAcceleration = math.mul(inverse, anchor.FeltAngularAcceleration);
            var inertia = -math.mul(inverse, anchor.FeltAcceleration) - math.cross(turningAcceleration, place)
                - 2f * math.cross(turning, speed) - math.cross(turning, math.cross(turning, place));
            var level = anchor.FeltVelocity;
            level.y = 0f;
            var lean = along * ClampLength(-spring.SpeedLean * math.mul(inverse, level), spring.SpeedLeanLimit);
            return gravity + inertia + lean;
        }

        // Steps a particle's speed and place toward its target by a spring of a frequency and a
        // damping ratio and a felt acceleration, the damping taken implicitly so a stiff spring stays
        // stable.
        public static void Spring(ref float3 place, ref float3 speed, float3 target, float3 felt, float frequency, float damping,
            float step)
        {
            var omega = 2f * math.PI * frequency;
            speed = (speed + step * (omega * omega * (target - place) + felt)) / (1f + 2f * damping * omega * step);
            place += speed * step;
        }

        // A value of a chain's curve at a share of the way from its root to its tip.
        public static float Along(float root, float tip, int index, int count)
        {
            return count > 1 ? math.lerp(root, tip, index / (float)(count - 1)) : root;
        }

        // Keeps a chain's particles, from its first at an offset, within their twist around the
        // anchor's upright axis from where the target lays them, and toward each neighbour, from
        // their first at offsets or -1 for none, within a share of the angle to the neighbour's
        // target, so the chains keep their order around the anchor.
        public static void LimitTwist(NativeArray<float3> places, NativeArray<float3> targets, int start, int bones, float3 up, float twist,
            int left, int leftBones, int right, int rightBones)
        {
            if (twist <= 0f)
            {
                return;
            }

            for (var index = 1; index <= bones; index++)
            {
                var place = places[start + index];
                var targetAcross = Across(targets[start + index], up);
                var across = Across(place, up);
                if (math.lengthsq(across) < TwistReach * TwistReach || math.lengthsq(targetAcross) < TwistReach * TwistReach)
                {
                    continue;
                }

                var lowest = -twist;
                var highest = twist;
                Toward(targets, left, leftBones, index, targetAcross, up, ref lowest, ref highest);
                Toward(targets, right, rightBones, index, targetAcross, up, ref lowest, ref highest);
                var angle = Turned(targetAcross, across, up);
                var limited = math.clamp(angle, lowest, math.max(lowest, highest));
                if (limited != angle)
                {
                    places[start + index] = ClothMath.AxisAngle(up, limited - angle, place);
                }
            }
        }

        // Narrows a particle's twist toward a neighbour to a share of the angle to the neighbour's
        // target at the same place along it.
        private static void Toward(NativeArray<float3> targets, int neighbour, int neighbourBones, int index, float3 targetAcross, float3 up,
            ref float lowest, ref float highest)
        {
            if (neighbour < 0 || index > neighbourBones)
            {
                return;
            }

            var other = Across(targets[neighbour + index], up);
            if (math.lengthsq(other) < TwistReach * TwistReach)
            {
                return;
            }

            var gap = Turned(targetAcross, other, up);
            if (gap > 0f)
            {
                highest = math.min(highest, NeighbourTwist * gap);
            }
            else
            {
                lowest = math.max(lowest, NeighbourTwist * gap);
            }
        }

        // A point's offset from an axis through the anchor.
        private static float3 Across(float3 point, float3 up)
        {
            return point - up * math.dot(point, up);
        }

        // The signed angle from one offset to another around an axis.
        private static float Turned(float3 from, float3 to, float3 up)
        {
            return math.atan2(math.dot(up, math.cross(from, to)), math.dot(from, to));
        }

        // Lays a chain's bones again from its root at their lengths in the target, each turned no
        // farther from where the target points it than its cone.
        public static void HoldBones(NativeArray<float3> places, NativeArray<float3> targets, int start, int bones, float coneRoot,
            float coneTip)
        {
            for (var index = 0; index < bones; index++)
            {
                var targetWay = targets[start + index + 1] - targets[start + index];
                var length = math.length(targetWay);
                if (length < 1e-6f)
                {
                    places[start + index + 1] = places[start + index];
                    continue;
                }

                var aim = targetWay / length;
                var way = places[start + index + 1] - places[start + index];
                var wayLength = math.length(way);
                var direction = wayLength > 1e-6f ? way / wayLength : aim;
                var cone = Along(coneRoot, coneTip, index, bones);
                var cosine = math.dot(direction, aim);
                if (cosine < math.cos(cone))
                {
                    var angle = math.acos(math.clamp(cosine, -1f, 1f));
                    direction = ClothMath.Normalized(ClothMath.SlerpDirections(aim, direction, cone / angle));
                    if (math.lengthsq(direction) < 0.5f)
                    {
                        direction = aim;
                    }
                }

                places[start + index + 1] = places[start + index] + direction * length;
            }
        }

        // Turns a chain's bones, from its root at an offset, no nearer the body than where the target
        // points them: no less the way the chain faces than the target's, each turned toward that way
        // as little as it takes. The legs and the body decide where the cloth is; the springs only
        // swing it out and to the side from there.
        public static void Floor(NativeArray<float3> places, NativeArray<float3> targets, int start, int bones, float3 outward)
        {
            for (var index = 0; index < bones; index++)
            {
                var targetWay = targets[start + index + 1] - targets[start + index];
                var length = math.length(targetWay);
                var way = places[start + index + 1] - places[start + index];
                var wayLength = math.length(way);
                if (length < 1e-6f || wayLength < 1e-6f)
                {
                    continue;
                }

                var direction = way / wayLength;
                var floor = math.dot(targetWay / length, outward);
                var along = math.dot(direction, outward);
                if (along >= floor)
                {
                    continue;
                }

                var axis = ClothMath.Normalized(math.cross(direction, outward));
                direction = math.lengthsq(axis) > 0.5f
                    ? ClothMath.AxisAngle(axis, math.acos(math.clamp(along, -1f, 1f)) - math.acos(math.clamp(floor, -1f, 1f)), direction)
                    : targetWay / length;
                places[start + index + 1] = places[start + index] + direction * wayLength;
            }
        }

        // Keeps the particles of two neighbouring chains, from their roots at offsets, between the
        // shares of how far apart the target lays them, each moved half the way.
        public static void Link(NativeArray<float3> places, NativeArray<float3> targets, int start, int otherStart, int count)
        {
            for (var index = 1; index < count; index++)
            {
                var way = places[start + index] - places[otherStart + index];
                var length = math.length(way);
                var rest = math.distance(targets[start + index], targets[otherStart + index]);
                if (length < 1e-6f || rest < 1e-6f)
                {
                    continue;
                }

                var kept = math.clamp(length, LinkShortest * rest, LinkLongest * rest);
                if (kept == length)
                {
                    continue;
                }

                var move = way * (0.5f * (length - kept) / length);
                places[start + index] -= move;
                places[otherStart + index] += move;
            }
        }

        public static bool IsFinite(float3 value)
        {
            return math.all(math.isfinite(value));
        }
    }
}
