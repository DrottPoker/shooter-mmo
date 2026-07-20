using System;
using System.Collections.Generic;

namespace ShooterMmo.GameSimulation
{
    public static class CollisionDataFormat
    {
        public const ushort Version = 1;
        public const uint ChunkMagic = 0x4C434D53;
        public const int MaximumBoxesPerChunk = 1_000_000;
    }

    public static class CollisionLayers
    {
        public const uint CharacterMovement = 1u << 0;
        public const uint CombatQueries = 1u << 1;
        public const uint All = uint.MaxValue;
    }

    public readonly struct SimulationVector3
    {
        public SimulationVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float LengthSquared
        {
            get { return (X * X) + (Y * Y) + (Z * Z); }
        }

        public float Length
        {
            get { return (float)Math.Sqrt(LengthSquared); }
        }

        public static SimulationVector3 Zero
        {
            get { return new SimulationVector3(0f, 0f, 0f); }
        }

        public static SimulationVector3 UnitX
        {
            get { return new SimulationVector3(1f, 0f, 0f); }
        }

        public static SimulationVector3 UnitY
        {
            get { return new SimulationVector3(0f, 1f, 0f); }
        }

        public static SimulationVector3 UnitZ
        {
            get { return new SimulationVector3(0f, 0f, 1f); }
        }

        public static SimulationVector3 operator +(SimulationVector3 left, SimulationVector3 right)
        {
            return new SimulationVector3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        }

        public static SimulationVector3 operator -(SimulationVector3 left, SimulationVector3 right)
        {
            return new SimulationVector3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        public static SimulationVector3 operator -(SimulationVector3 value)
        {
            return new SimulationVector3(-value.X, -value.Y, -value.Z);
        }

        public static SimulationVector3 operator *(SimulationVector3 value, float scale)
        {
            return new SimulationVector3(value.X * scale, value.Y * scale, value.Z * scale);
        }

        public static SimulationVector3 operator /(SimulationVector3 value, float scale)
        {
            return new SimulationVector3(value.X / scale, value.Y / scale, value.Z / scale);
        }

        public static float Dot(SimulationVector3 left, SimulationVector3 right)
        {
            return (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
        }

        public SimulationVector3 Normalized()
        {
            var length = Length;
            return length <= 0.000001f ? Zero : this / length;
        }

        public static SimulationVector3 Min(SimulationVector3 left, SimulationVector3 right)
        {
            return new SimulationVector3(
                Math.Min(left.X, right.X),
                Math.Min(left.Y, right.Y),
                Math.Min(left.Z, right.Z));
        }

        public static SimulationVector3 Max(SimulationVector3 left, SimulationVector3 right)
        {
            return new SimulationVector3(
                Math.Max(left.X, right.X),
                Math.Max(left.Y, right.Y),
                Math.Max(left.Z, right.Z));
        }

        public static SimulationVector3 Abs(SimulationVector3 value)
        {
            return new SimulationVector3(
                Math.Abs(value.X),
                Math.Abs(value.Y),
                Math.Abs(value.Z));
        }

        public static bool IsFinite(SimulationVector3 value)
        {
            return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public readonly struct SimulationQuaternion
    {
        private const float UnitLengthSquaredTolerance = 0.00001f;

        public SimulationQuaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float W { get; }

        public static SimulationQuaternion Identity
        {
            get { return new SimulationQuaternion(0f, 0f, 0f, 1f); }
        }

        public SimulationQuaternion Normalized()
        {
            var lengthSquared = (X * X) + (Y * Y) + (Z * Z) + (W * W);
            if (lengthSquared <= 0.000000000001f)
            {
                return Identity;
            }

            if (Math.Abs(lengthSquared - 1f) <= UnitLengthSquaredTolerance)
            {
                return this;
            }

            var length = (float)Math.Sqrt(lengthSquared);
            return new SimulationQuaternion(X / length, Y / length, Z / length, W / length);
        }

        public SimulationVector3 Rotate(SimulationVector3 value)
        {
            var quaternion = Normalized();
            var vector = new SimulationVector3(quaternion.X, quaternion.Y, quaternion.Z);
            var twiceCross = Cross(vector, value) * 2f;
            return value + (twiceCross * quaternion.W) + Cross(vector, twiceCross);
        }

        public SimulationVector3 InverseRotate(SimulationVector3 value)
        {
            var quaternion = Normalized();
            return new SimulationQuaternion(
                    -quaternion.X,
                    -quaternion.Y,
                    -quaternion.Z,
                    quaternion.W)
                .Rotate(value);
        }

        public static bool IsFinite(SimulationQuaternion value)
        {
            return IsFinite(value.X)
                && IsFinite(value.Y)
                && IsFinite(value.Z)
                && IsFinite(value.W);
        }

        private static SimulationVector3 Cross(SimulationVector3 left, SimulationVector3 right)
        {
            return new SimulationVector3(
                (left.Y * right.Z) - (left.Z * right.Y),
                (left.Z * right.X) - (left.X * right.Z),
                (left.X * right.Y) - (left.Y * right.X));
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public readonly struct CollisionAabb
    {
        public CollisionAabb(SimulationVector3 minimum, SimulationVector3 maximum)
        {
            if (!SimulationVector3.IsFinite(minimum)
                || !SimulationVector3.IsFinite(maximum)
                || minimum.X > maximum.X
                || minimum.Y > maximum.Y
                || minimum.Z > maximum.Z)
            {
                throw new ArgumentException("Collision bounds are invalid.");
            }

            Minimum = minimum;
            Maximum = maximum;
        }

        public SimulationVector3 Minimum { get; }

        public SimulationVector3 Maximum { get; }

        public bool Intersects(CollisionAabb other)
        {
            return Minimum.X <= other.Maximum.X
                && Maximum.X >= other.Minimum.X
                && Minimum.Y <= other.Maximum.Y
                && Maximum.Y >= other.Minimum.Y
                && Minimum.Z <= other.Maximum.Z
                && Maximum.Z >= other.Minimum.Z;
        }

        public CollisionAabb Expanded(float amount)
        {
            var expansion = new SimulationVector3(amount, amount, amount);
            return new CollisionAabb(Minimum - expansion, Maximum + expansion);
        }
    }

    public sealed class CollisionBox
    {
        public CollisionBox(
            ulong stableId,
            uint layerMask,
            SimulationVector3 center,
            SimulationVector3 halfExtents,
            SimulationQuaternion rotation)
        {
            if (stableId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stableId));
            }

            if (layerMask == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(layerMask));
            }

            if (!SimulationVector3.IsFinite(center)
                || !SimulationVector3.IsFinite(halfExtents)
                || halfExtents.X <= 0f
                || halfExtents.Y <= 0f
                || halfExtents.Z <= 0f)
            {
                throw new ArgumentException("Collision box dimensions are invalid.");
            }

            if (!SimulationQuaternion.IsFinite(rotation))
            {
                throw new ArgumentException("Collision box rotation is invalid.", nameof(rotation));
            }

            StableId = stableId;
            LayerMask = layerMask;
            Center = center;
            HalfExtents = halfExtents;
            Rotation = rotation.Normalized();
            Bounds = CalculateBounds(center, halfExtents, Rotation);
        }

        public ulong StableId { get; }

        public uint LayerMask { get; }

        public SimulationVector3 Center { get; }

        public SimulationVector3 HalfExtents { get; }

        public SimulationQuaternion Rotation { get; }

        public CollisionAabb Bounds { get; }

        private static CollisionAabb CalculateBounds(
            SimulationVector3 center,
            SimulationVector3 halfExtents,
            SimulationQuaternion rotation)
        {
            var axisX = SimulationVector3.Abs(rotation.Rotate(SimulationVector3.UnitX));
            var axisY = SimulationVector3.Abs(rotation.Rotate(SimulationVector3.UnitY));
            var axisZ = SimulationVector3.Abs(rotation.Rotate(SimulationVector3.UnitZ));
            var worldExtents = new SimulationVector3(
                (axisX.X * halfExtents.X) + (axisY.X * halfExtents.Y) + (axisZ.X * halfExtents.Z),
                (axisX.Y * halfExtents.X) + (axisY.Y * halfExtents.Y) + (axisZ.Y * halfExtents.Z),
                (axisX.Z * halfExtents.X) + (axisY.Z * halfExtents.Y) + (axisZ.Z * halfExtents.Z));
            return new CollisionAabb(center - worldExtents, center + worldExtents);
        }
    }

    [Serializable]
    public sealed class CollisionWorldManifest
    {
        public int FormatVersion;
        public string WorldId;
        public string Revision;
        public float ChunkSize;
        public CollisionChunkManifestEntry[] Chunks;
    }

    [Serializable]
    public sealed class CollisionChunkManifestEntry
    {
        public int X;
        public int Z;
        public string ResourceName;
        public string Sha256;
        public float MinimumX;
        public float MinimumZ;
        public float MaximumX;
        public float MaximumZ;
    }

    public sealed class CollisionQueryBuffer
    {
        private readonly List<CollisionBox> boxes = new List<CollisionBox>();
        private readonly HashSet<ulong> stableIds = new HashSet<ulong>();

        public IReadOnlyList<CollisionBox> Boxes
        {
            get { return boxes; }
        }

        public void Clear()
        {
            boxes.Clear();
            stableIds.Clear();
        }

        internal void Add(CollisionBox box)
        {
            if (stableIds.Add(box.StableId))
            {
                boxes.Add(box);
            }
        }

        internal void SortByStableId()
        {
            boxes.Sort((left, right) => left.StableId.CompareTo(right.StableId));
        }
    }

    public interface ICollisionWorld
    {
        void QueryBoxes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer);
    }
}
