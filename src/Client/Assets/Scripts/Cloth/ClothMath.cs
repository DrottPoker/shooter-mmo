using Unity.Mathematics;

namespace Mmorpg.Client.Cloth
{
    // The vector and rotation arithmetic of the cloth, written out so that the art pipeline's
    // tools/blender/art_pipeline/cloth_core.py follows it step by step: Unity's own ways to
    // normalize, to turn a vector by a rotation, and to blend rotations and directions.
    public static class ClothMath
    {
        // Below this length a vector has no direction, as Unity's Vector3.kEpsilon.
        public const float Epsilon = 1e-5f;

        // Rotations closer than this dot blend straight and normalized, as Quaternion.Slerp does.
        public const float StraightBlendDot = 0.95f;

        public static float3 Normalized(float3 value)
        {
            var length = math.length(value);
            return length > Epsilon ? value / length : float3.zero;
        }

        // A vector turned by a rotation, as Unity's Quaternion * Vector3 computes it.
        public static float3 Rotate(quaternion rotation, float3 value)
        {
            var q = rotation.value;
            float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
            float xx = q.x * x, yy = q.y * y, zz = q.z * z;
            float xy = q.x * y, xz = q.x * z, yz = q.y * z;
            float wx = q.w * x, wy = q.w * y, wz = q.w * z;
            return new float3(
                (1f - (yy + zz)) * value.x + (xy - wz) * value.y + (xz + wy) * value.z,
                (xy + wz) * value.x + (1f - (xx + zz)) * value.y + (yz - wx) * value.z,
                (xz - wy) * value.x + (yz + wx) * value.y + (1f - (xx + yy)) * value.z);
        }

        // The shorter way from one rotation to another by a clamped share, blended straight and
        // normalized when they lie close.
        public static quaternion Slerp(quaternion from, quaternion to, float share)
        {
            share = math.saturate(share);
            var a = from.value;
            var b = to.value;
            var dot = math.dot(a, b);
            if (dot < 0f)
            {
                dot = -dot;
                b = -b;
            }

            float4 mixed;
            if (dot > StraightBlendDot)
            {
                mixed = a + (b - a) * share;
            }
            else
            {
                var angle = math.acos(math.min(dot, 1f));
                var sin = math.sin(angle);
                mixed = a * (math.sin((1f - share) * angle) / sin) + b * (math.sin(share * angle) / sin);
            }

            return new quaternion(mixed / math.length(mixed));
        }

        // A direction turned from one toward another by a clamped share of the angle between them,
        // and the length taken between theirs, as Unity's Vector3.Slerp.
        public static float3 SlerpDirections(float3 from, float3 to, float share)
        {
            share = math.saturate(share);
            var lengthFrom = math.length(from);
            var lengthTo = math.length(to);
            if (lengthFrom < Epsilon || lengthTo < Epsilon)
            {
                return math.lerp(from, to, share);
            }

            var length = lengthFrom + (lengthTo - lengthFrom) * share;
            var dot = math.dot(from, to) / (lengthFrom * lengthTo);
            if (dot > 1f - Epsilon)
            {
                return math.lerp(from, to, share);
            }

            var unit = from / lengthFrom;
            if (dot < -1f + Epsilon)
            {
                return AxisAngle(Orthonormal(unit), math.PI * share, unit) * length;
            }

            return AxisAngle(Normalized(math.cross(from, to)), math.acos(dot) * share, unit) * length;
        }

        // A vector turned by an angle around a unit axis.
        public static float3 AxisAngle(float3 axis, float angle, float3 value)
        {
            math.sincos(angle, out var sin, out var cos);
            return value * cos + math.cross(axis, value) * sin + axis * (math.dot(axis, value) * (1f - cos));
        }

        // A unit vector at right angles to a unit vector.
        public static float3 Orthonormal(float3 value)
        {
            if (math.abs(value.z) > 0.70710678f)
            {
                var k = 1f / math.sqrt(value.y * value.y + value.z * value.z);
                return new float3(0f, -value.z * k, value.y * k);
            }

            var j = 1f / math.sqrt(value.x * value.x + value.y * value.y);
            return new float3(-value.y * j, value.x * j, 0f);
        }

        // The shortest rotation that turns one direction into another.
        public static quaternion FromTo(float3 from, float3 to)
        {
            var a = Normalized(from);
            var b = Normalized(to);
            var dot = math.dot(a, b);
            if (dot < -1f + Epsilon)
            {
                return quaternion.AxisAngle(Orthonormal(a), math.PI);
            }

            var axis = math.cross(a, b);
            return math.normalize(new quaternion(axis.x, axis.y, axis.z, 1f + dot));
        }

        // The point of a segment nearest a point.
        public static float3 Closest(float3 point, float3 a, float3 b)
        {
            var axis = b - a;
            var lengthSquared = math.lengthsq(axis);
            if (lengthSquared < 1e-12f)
            {
                return a;
            }

            return a + axis * math.saturate(math.dot(point - a, axis) / lengthSquared);
        }
    }
}
