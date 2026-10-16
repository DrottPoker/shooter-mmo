using System;
using System.Collections.Generic;

namespace Mmorpg.GameSimulation
{
    // Shared simulation math rounds every float intermediate with an explicit (float)
    // cast. Mono evaluates float expressions in double precision and rounds only
    // when a value is stored, while .NET rounds every operation, so without the casts
    // Unity prediction and the authoritative worker drift apart. A cast is a no-op on
    // .NET. See the movement determinism golden test.
    public static class CollisionDataFormat
    {
        public const ushort Version = 1;
        public const uint ChunkMagic = 0x4C434D53;
        public const int MaximumBoxesPerChunk = 1_000_000;

        // A chunk file is its manifest resource name plus this extension, on disk and as
        // the end of its Unity Addressables address.
        public const string ChunkFileExtension = ".bytes";

        // Every runtime must map a position to the same chunk coordinate. Worlds stay
        // inside a range where the float to int conversion is exact in .NET and Unity
        // and where chunk loops can never overflow.
        public const float MaximumWorldCoordinate = 1_000_000f;
        public const float MinimumChunkSize = 1f;
        public const float MaximumChunkSize = 4096f;
        public const int MaximumChunkCoordinate = 1 << 24;
        public const int MaximumChunksPerBox = 65_536;

        public static bool IsValidChunkSize(float chunkSize)
        {
            return chunkSize >= MinimumChunkSize && chunkSize <= MaximumChunkSize;
        }

        public static bool IsWithinWorldLimits(CollisionAabb bounds)
        {
            return Math.Abs(bounds.Minimum.X) <= MaximumWorldCoordinate
                && Math.Abs(bounds.Minimum.Y) <= MaximumWorldCoordinate
                && Math.Abs(bounds.Minimum.Z) <= MaximumWorldCoordinate
                && Math.Abs(bounds.Maximum.X) <= MaximumWorldCoordinate
                && Math.Abs(bounds.Maximum.Y) <= MaximumWorldCoordinate
                && Math.Abs(bounds.Maximum.Z) <= MaximumWorldCoordinate;
        }
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
            get { return (float)((float)((float)(X * X) + (float)(Y * Y)) + (float)(Z * Z)); }
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
            return new SimulationVector3(
                (float)(left.X + right.X),
                (float)(left.Y + right.Y),
                (float)(left.Z + right.Z));
        }

        public static SimulationVector3 operator -(SimulationVector3 left, SimulationVector3 right)
        {
            return new SimulationVector3(
                (float)(left.X - right.X),
                (float)(left.Y - right.Y),
                (float)(left.Z - right.Z));
        }

        public static SimulationVector3 operator -(SimulationVector3 value)
        {
            return new SimulationVector3(-value.X, -value.Y, -value.Z);
        }

        public static SimulationVector3 operator *(SimulationVector3 value, float scale)
        {
            return new SimulationVector3(
                (float)(value.X * scale),
                (float)(value.Y * scale),
                (float)(value.Z * scale));
        }

        public static SimulationVector3 operator /(SimulationVector3 value, float scale)
        {
            return new SimulationVector3(
                (float)(value.X / scale),
                (float)(value.Y / scale),
                (float)(value.Z / scale));
        }

        public static float Dot(SimulationVector3 left, SimulationVector3 right)
        {
            return (float)((float)((float)(left.X * right.X) + (float)(left.Y * right.Y))
                + (float)(left.Z * right.Z));
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
            var lengthSquared = (float)((float)((float)((float)(X * X) + (float)(Y * Y))
                    + (float)(Z * Z))
                + (float)(W * W));
            if (lengthSquared <= 0.000000000001f)
            {
                return Identity;
            }

            if (Math.Abs((float)(lengthSquared - 1f)) <= UnitLengthSquaredTolerance)
            {
                return this;
            }

            var length = (float)Math.Sqrt(lengthSquared);
            return new SimulationQuaternion(
                (float)(X / length),
                (float)(Y / length),
                (float)(Z / length),
                (float)(W / length));
        }

        public SimulationVector3 Rotate(SimulationVector3 value)
        {
            var quaternion = Normalized();
            var vector = new SimulationVector3(quaternion.X, quaternion.Y, quaternion.Z);
            var twiceCross = Cross(vector, value) * 2f;
            var scaled = twiceCross * quaternion.W;
            var rotated = value + scaled;
            return rotated + Cross(vector, twiceCross);
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
                (float)((float)(left.Y * right.Z) - (float)(left.Z * right.Y)),
                (float)((float)(left.Z * right.X) - (float)(left.X * right.Z)),
                (float)((float)(left.X * right.Y) - (float)(left.Y * right.X)));
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
            if (!CollisionDataFormat.IsWithinWorldLimits(Bounds))
            {
                throw new ArgumentException("Collision box lies outside the supported world limits.");
            }
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
                SumOfProducts(axisX.X, halfExtents.X, axisY.X, halfExtents.Y, axisZ.X, halfExtents.Z),
                SumOfProducts(axisX.Y, halfExtents.X, axisY.Y, halfExtents.Y, axisZ.Y, halfExtents.Z),
                SumOfProducts(axisX.Z, halfExtents.X, axisY.Z, halfExtents.Y, axisZ.Z, halfExtents.Z));
            return new CollisionAabb(center - worldExtents, center + worldExtents);
        }

        private static float SumOfProducts(
            float firstLeft,
            float firstRight,
            float secondLeft,
            float secondRight,
            float thirdLeft,
            float thirdRight)
        {
            return (float)((float)((float)(firstLeft * firstRight) + (float)(secondLeft * secondRight))
                + (float)(thirdLeft * thirdRight));
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
