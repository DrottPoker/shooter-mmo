using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mmorpg.GameSimulation
{
    public sealed class CollisionChunk
    {
        // Small chunks are scanned directly. Larger chunks, such as terrain with thousands
        // of triangles, index their shapes in a planar grid of roughly IndexCellSize cells.
        private const int LinearScanShapeLimit = 32;
        private const float IndexCellSize = 2f;
        private const int MaximumIndexCellsPerSide = 64;

        private readonly CollisionShape[] shapes;
        private readonly int[][] indexCells;
        private readonly int indexCellsPerSide;
        private readonly float indexCellSize;

        public CollisionChunk(
            int x,
            int z,
            CollisionAabb bounds,
            IReadOnlyList<CollisionShape> shapes)
        {
            if (shapes == null)
            {
                throw new ArgumentNullException(nameof(shapes));
            }

            X = x;
            Z = z;
            Bounds = bounds;
            this.shapes = shapes.ToArray();
            if (this.shapes.Any(shape => shape == null))
            {
                throw new ArgumentException("Collision chunk shapes must not be null.", nameof(shapes));
            }

            if (this.shapes.Length > LinearScanShapeLimit)
            {
                var width = Math.Max(
                    (float)(bounds.Maximum.X - bounds.Minimum.X),
                    (float)(bounds.Maximum.Z - bounds.Minimum.Z));
                indexCellsPerSide = Math.Max(
                    1,
                    Math.Min(MaximumIndexCellsPerSide, (int)Math.Ceiling((float)(width / IndexCellSize))));
                indexCellSize = (float)(width / indexCellsPerSide);
                indexCells = BuildIndex();
            }
        }

        public int X { get; }

        public int Z { get; }

        public CollisionAabb Bounds { get; }

        public IReadOnlyList<CollisionShape> Shapes
        {
            get { return shapes; }
        }

        internal void Query(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
        {
            if (!Bounds.Intersects(bounds))
            {
                return;
            }

            if (indexCells == null)
            {
                for (var index = 0; index < shapes.Length; index++)
                {
                    AddIfIntersecting(shapes[index], bounds, layerMask, buffer);
                }

                return;
            }

            GetCellRange(bounds, out var minimumX, out var maximumX, out var minimumZ, out var maximumZ);
            for (var cellZ = minimumZ; cellZ <= maximumZ; cellZ++)
            {
                for (var cellX = minimumX; cellX <= maximumX; cellX++)
                {
                    var cell = indexCells[(cellZ * indexCellsPerSide) + cellX];
                    for (var index = 0; index < cell.Length; index++)
                    {
                        AddIfIntersecting(shapes[cell[index]], bounds, layerMask, buffer);
                    }
                }
            }
        }

        private static void AddIfIntersecting(
            CollisionShape shape,
            CollisionAabb bounds,
            uint layerMask,
            CollisionQueryBuffer buffer)
        {
            if ((shape.LayerMask & layerMask) != 0 && shape.Bounds.Intersects(bounds))
            {
                buffer.Add(shape);
            }
        }

        private int[][] BuildIndex()
        {
            var cells = new List<int>[indexCellsPerSide * indexCellsPerSide];
            for (var shapeIndex = 0; shapeIndex < shapes.Length; shapeIndex++)
            {
                GetCellRange(
                    shapes[shapeIndex].Bounds,
                    out var minimumX,
                    out var maximumX,
                    out var minimumZ,
                    out var maximumZ);
                for (var cellZ = minimumZ; cellZ <= maximumZ; cellZ++)
                {
                    for (var cellX = minimumX; cellX <= maximumX; cellX++)
                    {
                        var cellIndex = (cellZ * indexCellsPerSide) + cellX;
                        if (cells[cellIndex] == null)
                        {
                            cells[cellIndex] = new List<int>();
                        }

                        cells[cellIndex].Add(shapeIndex);
                    }
                }
            }

            var result = new int[cells.Length][];
            for (var index = 0; index < cells.Length; index++)
            {
                result[index] = cells[index] == null ? Array.Empty<int>() : cells[index].ToArray();
            }

            return result;
        }

        // Bounds outside the chunk clamp to its edge cells, so a shape that only grazes
        // the chunk is still found.
        private void GetCellRange(
            CollisionAabb bounds,
            out int minimumX,
            out int maximumX,
            out int minimumZ,
            out int maximumZ)
        {
            minimumX = CellCoordinate(bounds.Minimum.X, Bounds.Minimum.X);
            maximumX = CellCoordinate(bounds.Maximum.X, Bounds.Minimum.X);
            minimumZ = CellCoordinate(bounds.Minimum.Z, Bounds.Minimum.Z);
            maximumZ = CellCoordinate(bounds.Maximum.Z, Bounds.Minimum.Z);
        }

        private int CellCoordinate(float position, float origin)
        {
            var cell = Math.Floor((float)((float)(position - origin) / indexCellSize));
            if (double.IsNaN(cell) || cell < 0d)
            {
                return 0;
            }

            return cell >= indexCellsPerSide ? indexCellsPerSide - 1 : (int)cell;
        }
    }

    // Version 2 layout after the header: the box count and boxes, then the triangle count
    // and triangles. Each list is ordered by stable id.
    public static class CollisionChunkCodec
    {
        public static byte[] Encode(CollisionChunk chunk)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            if (chunk.Shapes.Count > CollisionDataFormat.MaximumShapesPerChunk)
            {
                throw new ArgumentException("Collision chunk contains too many shapes.", nameof(chunk));
            }

            var boxes = chunk.Shapes.OfType<CollisionBox>().OrderBy(box => box.StableId).ToArray();
            var triangles = chunk.Shapes.OfType<CollisionTriangle>()
                .OrderBy(triangle => triangle.StableId)
                .ToArray();
            if (boxes.Length + triangles.Length != chunk.Shapes.Count)
            {
                throw new ArgumentException("Collision chunk contains an unsupported shape.", nameof(chunk));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CollisionDataFormat.ChunkMagic);
                writer.Write(CollisionDataFormat.Version);
                writer.Write(chunk.X);
                writer.Write(chunk.Z);
                WriteVector(writer, chunk.Bounds.Minimum);
                WriteVector(writer, chunk.Bounds.Maximum);
                writer.Write(boxes.Length);
                foreach (var box in boxes)
                {
                    writer.Write(box.StableId);
                    writer.Write(box.LayerMask);
                    WriteVector(writer, box.Center);
                    WriteVector(writer, box.HalfExtents);
                    writer.Write(box.Rotation.X);
                    writer.Write(box.Rotation.Y);
                    writer.Write(box.Rotation.Z);
                    writer.Write(box.Rotation.W);
                }

                writer.Write(triangles.Length);
                foreach (var triangle in triangles)
                {
                    writer.Write(triangle.StableId);
                    writer.Write(triangle.LayerMask);
                    WriteVector(writer, triangle.A);
                    WriteVector(writer, triangle.B);
                    WriteVector(writer, triangle.C);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static CollisionChunk Decode(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new InvalidDataException("Collision chunk is empty.");
            }

            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != CollisionDataFormat.ChunkMagic)
                    {
                        throw new InvalidDataException("Collision chunk magic is invalid.");
                    }

                    if (reader.ReadUInt16() != CollisionDataFormat.Version)
                    {
                        throw new InvalidDataException("Collision chunk version is unsupported.");
                    }

                    var x = reader.ReadInt32();
                    var z = reader.ReadInt32();
                    var minimum = ReadVector(reader);
                    var maximum = ReadVector(reader);
                    var bounds = new CollisionAabb(minimum, maximum);
                    var boxCount = ReadCount(reader, 0);
                    var shapes = new List<CollisionShape>(boxCount);
                    for (var index = 0; index < boxCount; index++)
                    {
                        shapes.Add(new CollisionBox(
                            reader.ReadUInt64(),
                            reader.ReadUInt32(),
                            ReadVector(reader),
                            ReadVector(reader),
                            new SimulationQuaternion(
                                reader.ReadSingle(),
                                reader.ReadSingle(),
                                reader.ReadSingle(),
                                reader.ReadSingle())));
                    }

                    var triangleCount = ReadCount(reader, boxCount);
                    for (var index = 0; index < triangleCount; index++)
                    {
                        shapes.Add(new CollisionTriangle(
                            reader.ReadUInt64(),
                            reader.ReadUInt32(),
                            ReadVector(reader),
                            ReadVector(reader),
                            ReadVector(reader)));
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("Collision chunk contains trailing data.");
                    }

                    return new CollisionChunk(x, z, bounds, shapes);
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Collision chunk is incomplete.", exception);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Collision chunk contains invalid geometry.", exception);
            }
        }

        private static int ReadCount(BinaryReader reader, int previousCount)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > CollisionDataFormat.MaximumShapesPerChunk - previousCount)
            {
                throw new InvalidDataException("Collision chunk shape count is invalid.");
            }

            return count;
        }

        private static void WriteVector(BinaryWriter writer, SimulationVector3 value)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        private static SimulationVector3 ReadVector(BinaryReader reader)
        {
            return new SimulationVector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
        }
    }
}
